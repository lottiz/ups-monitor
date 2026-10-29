using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Infrastructure.Sqlite;

namespace UPSControlCenter.SafetyAgent;

/// <summary>
/// Sincronizza la configurazione dal Control Plane (§7.4).
/// <para>
/// Il fallimento della sincronizzazione <b>non è un errore fatale</b>: l'agent continua con
/// l'ultima configurazione valida locale. È il requisito centrale di §3.3.
/// </para>
/// </summary>
public sealed class ConfigurationSynchronizer(
    SafetyStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<SafetyAgentOptions> options,
    SafetyConfigurationValidator validator,
    ILogger<ConfigurationSynchronizer> logger)
{
    private readonly SafetyAgentOptions _options = options.Value;
    private DateTimeOffset? _lastSuccessfulSync;

    public DateTimeOffset? LastSuccessfulSync => _lastSuccessfulSync;

    /// <summary>
    /// Carica la configurazione attiva dal database locale. Se manca o è corrotta restituisce
    /// <c>null</c>: il chiamante entra in <c>SafeHold</c> e non esegue azioni (§7.2).
    /// </summary>
    public SafetyConfiguration? LoadActiveConfiguration()
    {
        var stored = store.GetActiveConfig();
        if (stored is null)
        {
            logger.LogWarning(
                "Nessuna configurazione attiva nel database locale: l'agent resta in sola " +
                "osservazione (SafeHold) fino alla ricezione di una configurazione valida.");
            return null;
        }

        // La verifica di integrità viene rifatta al caricamento, non solo alla ricezione: il file
        // potrebbe essere stato corrotto a riposo da un problema di storage (R11).
        var integrity = VerifyIntegrity(stored.PayloadJson, stored.ChecksumSha256, stored.Signature);
        if (!integrity.IsValid)
        {
            logger.LogError(
                "La configurazione attiva versione {Version} non supera la verifica di " +
                "integrità: {Errors}. L'agent entra in SafeHold.",
                stored.Version, string.Join("; ", integrity.Errors));

            store.RecordCriticalEvent(UpsEventType.ConfigurationRejected, EventSeverity.Critical,
                null, $"Configurazione attiva {stored.Version} corrotta: " +
                      string.Join("; ", integrity.Errors), DateTimeOffset.UtcNow);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SafetyConfiguration>(
                stored.PayloadJson, RuleSerialization.Options);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "La configurazione attiva versione {Version} non è deserializzabile. SafeHold.",
                stored.Version);
            return null;
        }
    }

    /// <summary>
    /// Scarica e applica eventuali nuove configurazioni. Restituisce la configurazione attiva
    /// dopo il tentativo, che resta la precedente se la nuova non è valida.
    /// </summary>
    public async Task<SafetyConfiguration?> TrySyncAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ControlPlaneBaseUrl))
            return LoadActiveConfiguration();

        SafetyConfigEnvelope? envelope;
        try
        {
            var client = httpClientFactory.CreateClient("ControlPlane");
            client.BaseAddress = new Uri(_options.ControlPlaneBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(_options.AgentToken))
                client.DefaultRequestHeaders.Add("X-Agent-Token", _options.AgentToken);

            envelope = await client.GetFromJsonAsync<SafetyConfigEnvelope>(
                "api/safety/config/current", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or UriFormatException)
        {
            // Control Plane non raggiungibile: scenario previsto e gestito, non un guasto.
            logger.LogInformation(
                "Control Plane non raggiungibile ({Error}): si prosegue con la configurazione " +
                "locale attiva.", ex.Message);

            WarnIfSyncStale();
            return LoadActiveConfiguration();
        }

        if (envelope is null)
        {
            WarnIfSyncStale();
            return LoadActiveConfiguration();
        }

        _lastSuccessfulSync = DateTimeOffset.UtcNow;

        var currentVersion = store.GetActiveConfig()?.Version ?? 0;
        if (envelope.Version <= currentVersion)
            return LoadActiveConfiguration();

        var applied = ApplyEnvelope(envelope);

        if (applied is not null)
            await ConfirmAsync(envelope.Version, ct);

        return applied ?? LoadActiveConfiguration();
    }

    /// <summary>
    /// Conferma ricezione e attivazione al Control Plane. Sono due chiamate distinte perché
    /// rappresentano due fatti diversi: aver scaricato una configurazione e averla attivata (§3.3).
    /// Il fallimento di queste conferme non ha conseguenze sul funzionamento dell'agent.
    /// </summary>
    private async Task ConfirmAsync(long version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ControlPlaneBaseUrl)) return;

        try
        {
            var client = httpClientFactory.CreateClient("ControlPlane");
            client.BaseAddress = new Uri(_options.ControlPlaneBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);

            if (!string.IsNullOrWhiteSpace(_options.AgentToken))
                client.DefaultRequestHeaders.Add("X-Agent-Token", _options.AgentToken);

            var body = new ConfigAckRequest
            {
                Version = version,
                AgentInstanceId = $"{Environment.MachineName}:{Environment.ProcessId}"
            };

            await client.PostAsJsonAsync($"api/safety/config/{version}/ack", body, ct);
            await client.PostAsJsonAsync($"api/safety/config/{version}/activated", body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or UriFormatException)
        {
            logger.LogDebug("Conferme di configurazione non inviate: {Error}.", ex.Message);
        }
    }

    /// <summary>
    /// Verifica e applica una configurazione ricevuta. In caso di esito negativo la registra come
    /// rifiutata <b>senza toccare quella attiva</b>: nessuna applicazione parziale (§3.3).
    /// </summary>
    internal SafetyConfiguration? ApplyEnvelope(SafetyConfigEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var now = DateTimeOffset.UtcNow;
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (FormatException)
        {
            store.RejectConfig(envelope.Version, envelope.CanonicalPayload,
                envelope.ChecksumSha256, [], "Firma non codificata correttamente in Base64.", now);
            return null;
        }

        var integrity = VerifyIntegrity(envelope.CanonicalPayload, envelope.ChecksumSha256, signature);
        if (!integrity.IsValid)
        {
            var reason = string.Join("; ", integrity.Errors);
            store.RejectConfig(envelope.Version, envelope.CanonicalPayload,
                envelope.ChecksumSha256, signature, reason, now);
            store.RecordCriticalEvent(UpsEventType.ConfigurationRejected, EventSeverity.Critical,
                null, $"Versione {envelope.Version} rifiutata: {reason}", now);
            return null;
        }

        SafetyConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<SafetyConfiguration>(
                envelope.CanonicalPayload, RuleSerialization.Options);
        }
        catch (JsonException ex)
        {
            store.RejectConfig(envelope.Version, envelope.CanonicalPayload,
                envelope.ChecksumSha256, signature,
                $"Payload non deserializzabile: {ex.Message}", now);
            return null;
        }

        if (config is null)
        {
            store.RejectConfig(envelope.Version, envelope.CanonicalPayload,
                envelope.ChecksumSha256, signature, "Payload vuoto.", now);
            return null;
        }

        // Seconda validazione lato agent: non si fida della validazione già fatta dal portale,
        // perché il payload potrebbe provenire da una versione differente del Control Plane.
        var validation = validator.Validate(config);
        if (!validation.IsValid)
        {
            var reason = string.Join("; ", validation.Errors);
            store.RejectConfig(envelope.Version, envelope.CanonicalPayload,
                envelope.ChecksumSha256, signature, reason, now);
            store.RecordCriticalEvent(UpsEventType.ConfigurationRejected, EventSeverity.Critical,
                null, $"Versione {envelope.Version} non valida: {reason}", now);
            return null;
        }

        store.ActivateConfig(envelope.Version, envelope.CanonicalPayload,
            envelope.ChecksumSha256, signature, now);

        foreach (var warning in validation.Warnings)
            logger.LogWarning("Configurazione {Version}: {Warning}", envelope.Version, warning);

        return config;
    }

    private IntegrityVerification VerifyIntegrity(string payload, string checksum, byte[] signature)
    {
        if (_options.AllowUnsignedConfigForDevelopment)
        {
            logger.LogWarning(
                "Verifica della firma disattivata (AllowUnsignedConfigForDevelopment): " +
                "configurazione NON autenticata. Non utilizzare in produzione.");

            var actual = ConfigurationIntegrity.ComputeChecksum(payload);
            return string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase)
                ? IntegrityVerification.Ok()
                : IntegrityVerification.Invalid("Checksum SHA-256 non corrispondente.");
        }

        if (string.IsNullOrWhiteSpace(_options.ConfigHmacKeyBase64))
        {
            return IntegrityVerification.Invalid(
                "Chiave HMAC non configurata: impossibile autenticare la configurazione. " +
                "Impostare SafetyAgent:ConfigHmacKeyBase64.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(_options.ConfigHmacKeyBase64);
        }
        catch (FormatException)
        {
            return IntegrityVerification.Invalid("Chiave HMAC non valida: Base64 non decodificabile.");
        }

        if (key.Length < 32)
            return IntegrityVerification.Invalid(
                $"Chiave HMAC troppo corta ({key.Length} byte): minimo 32.");

        return ConfigurationIntegrity.Verify(payload, checksum, signature, key);
    }

    /// <summary>
    /// Registra un evento se la sincronizzazione manca da troppo tempo. Il comportamento
    /// dell'agent non cambia: cambia solo la visibilità del problema (§7.4).
    /// </summary>
    private void WarnIfSyncStale()
    {
        if (_lastSuccessfulSync is not { } last) return;

        var age = DateTimeOffset.UtcNow - last;
        if (age <= TimeSpan.FromSeconds(900)) return;

        logger.LogWarning(
            "Configurazione non sincronizzata da {Minutes:F0} minuti. L'agent continua con " +
            "l'ultima configurazione valida.", age.TotalMinutes);

        store.RecordCriticalEvent(UpsEventType.ConfigSyncStale, EventSeverity.Warning, null,
            $"Nessuna sincronizzazione da {age.TotalMinutes:F0} minuti.", DateTimeOffset.UtcNow);

        // Si azzera il riferimento per non ripetere l'evento a ogni ciclo.
        _lastSuccessfulSync = DateTimeOffset.UtcNow;
    }
}
