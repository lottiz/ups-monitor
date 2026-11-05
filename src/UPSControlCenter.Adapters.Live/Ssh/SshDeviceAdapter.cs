using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Adapters.Live.Ssh;

/// <summary>
/// Adapter di spegnimento via SSH per server Linux e NAS QNAP (§8.2, §8.8).
/// <para>
/// <b>Non esegue mai accensione</b>: SSH presuppone un sistema operativo già attivo. L'accensione
/// di un dispositivo spento richiede Wake-on-LAN, Redfish/BMC o un riavvio automatico dal BIOS
/// (§12) — mai questo adapter.
/// </para>
/// <para>
/// <b>Nessuna connessione senza fingerprint pinnato.</b> Se il profilo di connessione non ha un
/// fingerprint registrato, <see cref="ShutdownAsync"/> e <see cref="CheckStateAsync"/> si
/// rifiutano di procedere: l'unico modo per osservare e registrare il fingerprint di un host
/// nuovo è <see cref="TestConnectionAsync"/>, che richiede comunque una conferma esplicita
/// dell'amministratore prima che venga usato per azioni reali (§8.4, divieto di
/// <c>StrictHostKeyChecking=no</c>).
/// </para>
/// </summary>
public sealed class SshDeviceAdapter(
    SshShutdownProfile profile,
    ISshSessionFactory sessionFactory,
    ICredentialProtector credentialProtector,
    ILogger<SshDeviceAdapter> logger) : IDeviceAdapter
{
    private const int DefaultSshPort = 22;

    public string AdapterName => profile.AdapterName;
    public bool PerformsRealOperations => true;
    public ShutdownProtocol? SupportedShutdownProtocol => profile.Protocol;

    public bool CanHandle(ManagedDevice device) => device.ShutdownProtocol == profile.Protocol;

    public async Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var prep = Prepare(device, requirePinnedFingerprint: true);
        if (prep.Failure is not null) return prep.Failure;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), prep.Credential!, prep.FingerprintPolicy!,
                TimeSpan.FromSeconds(30), ct);

            var timeout = TimeSpan.FromSeconds(Math.Max(5, device.ShutdownTimeoutSeconds));
            var result = await session.ExecuteAsync(profile.ShutdownCommand, timeout, ct);

            if (result.DisconnectedDuringExecution)
            {
                return AdapterResult.Ok("SSH_SHUTDOWN_DISCONNECTED", dryRun: false,
                    "Connessione interrotta dopo l'invio del comando: esito atteso per un " +
                    "arresto, non un errore.", DeviceState.ShuttingDown);
            }

            if (result.ExitStatus is not 0)
            {
                logger.LogWarning(
                    "Comando di spegnimento su «{Device}» terminato con uscita {Exit}: {Error}",
                    device.Name, result.ExitStatus, result.Error);
                return AdapterResult.Fail("SSH_COMMAND_FAILED", dryRun: false,
                    $"Il comando è stato eseguito ma ha restituito l'uscita {result.ExitStatus}: " +
                    $"{result.Error}");
            }

            return AdapterResult.Ok("SSH_SHUTDOWN_OK", dryRun: false,
                $"Comando «{profile.ShutdownCommand}» eseguito con successo.", DeviceState.ShuttingDown);
        }
        catch (SshHostKeyRejectedException ex)
        {
            logger.LogError("Fingerprint della chiave host di «{Device}» rifiutato: {Fingerprint}",
                device.Name, ex.Fingerprint);
            return AdapterResult.Fail("SSH_HOST_KEY_MISMATCH", dryRun: false,
                "La chiave host presentata non corrisponde al fingerprint registrato: " +
                "possibile intercettazione o host sostituito. Connessione rifiutata.");
        }
        catch (SshAuthenticationException)
        {
            return AdapterResult.Fail("SSH_AUTH_FAILED", dryRun: false,
                "Autenticazione SSH non riuscita: verificare credenziali o chiave.");
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            return AdapterResult.Fail("SSH_UNREACHABLE", dryRun: false,
                $"Dispositivo non raggiungibile via SSH: {ex.Message}");
        }
    }

    /// <summary>SSH non supporta l'accensione: non ha senso provare a connettersi a un dispositivo spento.</summary>
    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Fail("SSH_CANNOT_START", dryRun: false,
            "SSH non può accendere un dispositivo spento: usare Wake-on-LAN, Redfish o BMC."));

    public async Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var prep = Prepare(device, requirePinnedFingerprint: true);
        if (prep.Failure is not null) return prep.Failure;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), prep.Credential!, prep.FingerprintPolicy!,
                TimeSpan.FromSeconds(10), ct);

            // Un comando innocuo, mai distruttivo: la connessione riuscita è già la prova che
            // il sistema operativo è attivo e accetta sessioni.
            await session.ExecuteAsync("true", TimeSpan.FromSeconds(5), ct);

            return AdapterResult.Ok("SSH_CHECK_ON", dryRun: false,
                "Connessione SSH riuscita: il sistema risponde.", DeviceState.On);
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            // Non raggiungibile non è prova di arresto (§10): resta Unreachable, non Off.
            return AdapterResult.Ok("SSH_CHECK_UNREACHABLE", dryRun: false,
                $"SSH non raggiungibile: {ex.Message}", DeviceState.Unreachable);
        }
        catch (SshHostKeyRejectedException)
        {
            return AdapterResult.Ok("SSH_CHECK_HOST_KEY_MISMATCH", dryRun: false,
                "Fingerprint della chiave host non corrispondente: stato non verificabile in sicurezza.",
                DeviceState.Unknown);
        }
    }

    public async Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return AdapterResult.Fail("SSH_NO_CREDENTIALS", dryRun: false,
                "Nessun profilo di connessione valido associato al dispositivo.");
        }

        var pinned = device.ConnectionProfile?.Fingerprint;
        string? observed = null;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), credential,
                fingerprintPolicy: fp =>
                {
                    observed = fp;
                    // Se un fingerprint è già pinnato, il test lo verifica rigorosamente, come
                    // farebbero le operazioni reali. Se non lo è, si accetta per questa sola
                    // volta (trust-on-first-use) al solo scopo di osservarlo e riportarlo:
                    // l'amministratore deve ancora confermarlo esplicitamente prima che possa
                    // essere usato per un'azione reale (§8.4).
                    return string.IsNullOrWhiteSpace(pinned) || string.Equals(pinned, fp, StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10), ct);

            await session.ExecuteAsync("true", TimeSpan.FromSeconds(5), ct);

            if (string.IsNullOrWhiteSpace(pinned))
            {
                return AdapterResult.Ok("SSH_TEST_FINGERPRINT_OBSERVED", dryRun: false,
                    $"Connessione riuscita. Fingerprint osservato: {observed}. Non è ancora " +
                    "registrato: nessuna azione reale sarà possibile finché non viene salvato " +
                    "esplicitamente nel profilo di connessione.", DeviceState.On);
            }

            return AdapterResult.Ok("SSH_TEST_OK", dryRun: false,
                "Connessione riuscita, fingerprint della chiave host verificato.", DeviceState.On);
        }
        catch (SshHostKeyRejectedException)
        {
            return AdapterResult.Fail("SSH_TEST_HOST_KEY_MISMATCH", dryRun: false,
                $"Il fingerprint osservato ({observed}) non corrisponde a quello registrato " +
                $"({pinned}). Connessione rifiutata: possibile host sostituito.");
        }
        catch (SshAuthenticationException)
        {
            return AdapterResult.Fail("SSH_TEST_AUTH_FAILED", dryRun: false,
                "Connessione stabilita ma autenticazione non riuscita.");
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            return AdapterResult.Fail("SSH_TEST_UNREACHABLE", dryRun: false,
                $"Dispositivo non raggiungibile: {ex.Message}");
        }
    }

    private sealed record Preparation
    {
        public ResolvedCredential? Credential { get; init; }
        public Func<string, bool>? FingerprintPolicy { get; init; }
        public AdapterResult? Failure { get; init; }
    }

    private Preparation Prepare(ManagedDevice device, bool requirePinnedFingerprint)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return new Preparation
            {
                Failure = AdapterResult.Fail("SSH_NO_CREDENTIALS", dryRun: false,
                    "Nessun profilo di connessione valido o segreto non decifrabile.")
            };
        }

        var pinned = device.ConnectionProfile?.Fingerprint;
        if (requirePinnedFingerprint && string.IsNullOrWhiteSpace(pinned))
        {
            // Nessuna connessione reale senza un fingerprint registrato: è l'applicazione del
            // divieto di StrictHostKeyChecking=no (§8.4). Usare "Test connessione" per
            // osservarlo e registrarlo esplicitamente prima di poter agire.
            return new Preparation
            {
                Failure = AdapterResult.Fail("SSH_FINGERPRINT_NOT_PINNED", dryRun: false,
                    "Nessun fingerprint della chiave host registrato per questo profilo: " +
                    "eseguire «Test connessione» e salvarlo esplicitamente prima di procedere.")
            };
        }

        return new Preparation
        {
            Credential = credential,
            FingerprintPolicy = fp => string.Equals(pinned, fp, StringComparison.Ordinal)
        };
    }

    private int ResolvePort(ManagedDevice device) => device.ManagementPort ?? DefaultSshPort;

    private static bool IsConnectivityException(Exception ex) =>
        ex is System.Net.Sockets.SocketException or SshOperationTimeoutException
            or SshConnectionException or TaskCanceledException or TimeoutException;
}
