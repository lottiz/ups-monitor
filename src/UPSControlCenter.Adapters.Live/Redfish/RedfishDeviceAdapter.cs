using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Adapters.Live.Redfish;

/// <summary>
/// Adapter per server con controller di gestione compatibili Redfish (DMTF), come il Lenovo
/// XClarity Controller 2 (§8.2, §12).
/// <para>
/// Usa solo le operazioni standard Redfish su <c>ComputerSystem.Reset</c>: <c>GracefulShutdown</c>
/// per lo spegnimento, <c>On</c> per l'accensione. <b>Non invoca mai <c>ForceOff</c></b> (§8.5):
/// l'opzione non è nemmeno rappresentabile da questo adapter, non solo evitata per convenzione.
/// </para>
/// <para>
/// <b>Nessuna connessione senza un fingerprint del certificato pinnato</b> (campo
/// <see cref="ConnectionProfile.Fingerprint"/>, riusato qui per il thumbprint SHA-256 del
/// certificato TLS). I BMC usano quasi sempre certificati autofirmati: fidarsi di un
/// certificato non verificato equivarrebbe a <c>StrictHostKeyChecking=no</c>, vietato per
/// analogia da §8.4. <see cref="TestConnectionAsync"/> è l'unico percorso che osserva e riporta
/// il thumbprint di un host nuovo, senza eseguire azioni reali.
/// </para>
/// </summary>
public sealed class RedfishDeviceAdapter(
    ICredentialProtector credentialProtector,
    ILogger<RedfishDeviceAdapter> logger) : IDeviceAdapter
{
    public const string Name = "Redfish";
    private const int DefaultHttpsPort = 443;
    private const string SystemsPath = "/redfish/v1/Systems/1";

    public string AdapterName => Name;
    public bool PerformsRealOperations => true;
    public ShutdownProtocol? SupportedShutdownProtocol => ShutdownProtocol.Redfish;
    public StartupProtocol? SupportedStartupProtocol => StartupProtocol.Redfish;

    public bool CanHandle(ManagedDevice device) =>
        device.ShutdownProtocol == ShutdownProtocol.Redfish
        || device.StartupProtocol == StartupProtocol.Redfish;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default) =>
        ResetAsync(device, "GracefulShutdown", DeviceState.ShuttingDown, ct);

    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default) =>
        ResetAsync(device, "On", DeviceState.Starting, ct);

    private async Task<AdapterResult> ResetAsync(ManagedDevice device, string resetType,
        DeviceState expectedState, CancellationToken ct)
    {
        var prep = Prepare(device, requirePinnedCertificate: true);
        if (prep.Failure is not null) return prep.Failure;

        var (client, observed) = CreateClient(device, prep.Credential!, allowTrustOnFirstUse: false);
        using (client)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { ResetType = resetType });

                using var response = await client.PostAsync(
                    $"{SystemsPath}/Actions/ComputerSystem.Reset",
                    new StringContent(body, Encoding.UTF8, "application/json"), ct);

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await SafeReadBodyAsync(response, ct);
                    logger.LogWarning(
                        "Redfish Reset({ResetType}) su «{Device}» ha risposto {Status}: {Detail}",
                        resetType, device.Name, response.StatusCode, detail);
                    return AdapterResult.Fail("REDFISH_RESET_FAILED", dryRun: false,
                        $"Il BMC ha risposto {(int)response.StatusCode}: {detail}");
                }

                return AdapterResult.Ok("REDFISH_RESET_OK", dryRun: false,
                    $"Azione Reset({resetType}) accettata dal BMC.", expectedState);
            }
            catch (HttpRequestException ex)
            {
                return MapHttpFailure(ex, device, observed(), "REDFISH_RESET");
            }
            catch (TaskCanceledException)
            {
                return AdapterResult.Fail("REDFISH_TIMEOUT", dryRun: false, "Timeout nella richiesta al BMC.");
            }
        }
    }

    public async Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var prep = Prepare(device, requirePinnedCertificate: true);
        if (prep.Failure is not null) return prep.Failure;

        var (client, observed) = CreateClient(device, prep.Credential!, allowTrustOnFirstUse: false);
        using (client)
        {
            try
            {
                using var response = await client.GetAsync(SystemsPath, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // Non raggiungibile o non autorizzato non è prova di arresto (§10).
                    return AdapterResult.Ok("REDFISH_CHECK_UNREACHABLE", dryRun: false,
                        $"Il BMC ha risposto {(int)response.StatusCode}: stato non verificabile.",
                        DeviceState.Unreachable);
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("PowerState", out var powerStateProp))
                {
                    return AdapterResult.Ok("REDFISH_CHECK_UNKNOWN", dryRun: false,
                        "Risposta Redfish senza il campo PowerState: stato non determinabile.",
                        DeviceState.Unknown);
                }

                var powerState = powerStateProp.GetString();
                var state = powerState switch
                {
                    "On" => DeviceState.On,
                    "Off" => DeviceState.Off,
                    _ => DeviceState.Unknown
                };

                return AdapterResult.Ok("REDFISH_CHECK_OK", dryRun: false,
                    $"PowerState riportato dal BMC: {powerState ?? "assente"}.", state);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Non raggiungibile non è prova di arresto (§10): resta Unreachable, non Off.
                return AdapterResult.Ok("REDFISH_CHECK_ERROR", dryRun: false,
                    $"Verifica non riuscita: {ex.Message}", DeviceState.Unreachable);
            }
        }
    }

    /// <summary>
    /// Richiede solo la radice del servizio Redfish: un'operazione di lettura innocua, senza
    /// toccare lo stato di alimentazione (§7). È l'unico metodo che accetta un certificato non
    /// ancora pinnato, al solo scopo di osservarlo e riportarlo.
    /// </summary>
    public async Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return AdapterResult.Fail("REDFISH_NO_CREDENTIALS", dryRun: false,
                "Nessun profilo di connessione valido o segreto non decifrabile.");
        }

        var pinned = device.ConnectionProfile?.Fingerprint;
        var (client, observed) = CreateClient(device, credential, allowTrustOnFirstUse: true);

        using (client)
        {
            try
            {
                using var response = await client.GetAsync("/redfish/v1/", ct);

                if (!response.IsSuccessStatusCode)
                {
                    return AdapterResult.Fail("REDFISH_TEST_FAILED", dryRun: false,
                        $"Il BMC ha risposto {(int)response.StatusCode}.");
                }

                if (string.IsNullOrWhiteSpace(pinned))
                {
                    return AdapterResult.Ok("REDFISH_TEST_CERT_OBSERVED", dryRun: false,
                        $"Connessione riuscita. Thumbprint del certificato osservato: " +
                        $"{observed()}. Non è ancora registrato: nessuna azione reale sarà " +
                        "possibile finché non viene salvato esplicitamente nel profilo.");
                }

                return AdapterResult.Ok("REDFISH_TEST_OK", dryRun: false,
                    "Servizio Redfish raggiungibile, certificato verificato, autenticazione accettata.");
            }
            catch (HttpRequestException ex)
            {
                return MapHttpFailure(ex, device, observed(), "REDFISH_TEST");
            }
            catch (TaskCanceledException)
            {
                return AdapterResult.Fail("REDFISH_TEST_TIMEOUT", dryRun: false, "Timeout nel test.");
            }
        }
    }

    /// <summary>
    /// Distingue un certificato non corrispondente (motivo di sicurezza, da segnalare con
    /// chiarezza) da una generica irraggiungibilità di rete.
    /// </summary>
    private AdapterResult MapHttpFailure(HttpRequestException ex, ManagedDevice device,
        string? observedThumbprint, string codePrefix)
    {
        var pinned = device.ConnectionProfile?.Fingerprint;

        if (observedThumbprint is not null && !string.IsNullOrWhiteSpace(pinned)
            && !string.Equals(pinned, observedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Certificato TLS di «{Device}» non corrispondente al thumbprint registrato " +
                "(osservato {Observed}, atteso {Pinned}).", device.Name, observedThumbprint, pinned);
            return AdapterResult.Fail($"{codePrefix}_CERT_MISMATCH", dryRun: false,
                "Il certificato presentato dal BMC non corrisponde al thumbprint registrato: " +
                "possibile intercettazione o host sostituito. Connessione rifiutata.");
        }

        return AdapterResult.Fail($"{codePrefix}_UNREACHABLE", dryRun: false,
            $"BMC non raggiungibile: {ex.Message}");
    }

    private sealed record Preparation
    {
        public ResolvedCredential? Credential { get; init; }
        public AdapterResult? Failure { get; init; }
    }

    private Preparation Prepare(ManagedDevice device, bool requirePinnedCertificate)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return new Preparation
            {
                Failure = AdapterResult.Fail("REDFISH_NO_CREDENTIALS", dryRun: false,
                    "Nessun profilo di connessione valido o segreto non decifrabile.")
            };
        }

        if (requirePinnedCertificate && string.IsNullOrWhiteSpace(device.ConnectionProfile?.Fingerprint))
        {
            return new Preparation
            {
                Failure = AdapterResult.Fail("REDFISH_CERT_NOT_PINNED", dryRun: false,
                    "Nessun thumbprint del certificato registrato per questo profilo: " +
                    "eseguire «Test connessione» e salvarlo esplicitamente prima di procedere.")
            };
        }

        return new Preparation { Credential = credential };
    }

    /// <summary>
    /// Crea un client HTTPS con verifica del certificato basata su fingerprint pinnato, non su
    /// una CA.
    /// </summary>
    /// <param name="allowTrustOnFirstUse">
    /// Solo per <see cref="TestConnectionAsync"/>: se vero, accetta il certificato anche senza
    /// un fingerprint pinnato, per poterlo osservare e riportare.
    /// </param>
    private static (HttpClient Client, Func<string?> GetObservedThumbprint) CreateClient(
        ManagedDevice device, ResolvedCredential credential, bool allowTrustOnFirstUse)
    {
        var pinned = device.ConnectionProfile?.Fingerprint;
        string? observed = null;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;

                observed = Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

                return string.IsNullOrWhiteSpace(pinned)
                    ? allowTrustOnFirstUse
                    : string.Equals(pinned, observed, StringComparison.OrdinalIgnoreCase);
            }
        };

        var client = new HttpClient(handler, disposeHandler: true);
        var port = device.ManagementPort ?? DefaultHttpsPort;
        client.BaseAddress = new UriBuilder("https", device.Hostname, port).Uri;
        client.Timeout = TimeSpan.FromSeconds(20);

        var authBytes = Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        return (client, () => observed);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "(corpo della risposta non leggibile)";
        }
    }
}
