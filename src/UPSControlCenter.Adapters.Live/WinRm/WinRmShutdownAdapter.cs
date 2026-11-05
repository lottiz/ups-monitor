using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Adapters.Live.WinRm;

/// <summary>
/// Adapter di spegnimento via WinRM per server Windows (§8.2, §24).
/// <para>
/// <b>Un solo comando fisso</b>, mai configurabile né derivato da input dell'utente: esattamente
/// come l'adapter SSH (§8.3), non è un canale di shell remota generico. Lo spegnimento invia
/// sempre <c>shutdown /s /f /t 0</c>; non esiste alcun percorso per eseguire un comando diverso.
/// </para>
/// <para>
/// <b>Solo HTTPS, nessuna connessione senza thumbprint TLS pinnato</b>, con lo stesso schema di
/// trust-on-first-use riservato esclusivamente a <see cref="TestConnectionAsync"/> usato per SSH
/// e Redfish (§8.4, divieto di <c>StrictHostKeyChecking=no</c>).
/// </para>
/// <para>
/// <b>Non supporta l'accensione</b>: WinRM presuppone un sistema operativo Windows già in
/// esecuzione. Per l'accensione di un host spento serve Wake-on-LAN, Redfish/BMC o il riavvio
/// automatico da BIOS.
/// </para>
/// </summary>
public sealed class WinRmShutdownAdapter(
    IWinRmSessionFactory sessionFactory,
    ICredentialProtector credentialProtector,
    ILogger<WinRmShutdownAdapter> logger) : IDeviceAdapter
{
    public const string Name = "WinRm";
    private const int DefaultWinRmHttpsPort = 5986;
    private const string ShutdownCommand = "shutdown /s /f /t 0";
    private const string ProbeCommand = "hostname";

    public string AdapterName => Name;
    public bool PerformsRealOperations => true;
    public ShutdownProtocol? SupportedShutdownProtocol => ShutdownProtocol.WinRm;

    public bool CanHandle(ManagedDevice device) =>
        device.ShutdownProtocol == ShutdownProtocol.WinRm
        || device.StatusCheckMethod == StatusCheckMethod.WinRm;

    public async Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var prep = Prepare(device, requirePinnedCertificate: true);
        if (prep.Failure is not null) return prep.Failure;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), prep.Credential!, prep.CertificatePolicy!,
                TimeSpan.FromSeconds(30), ct);

            var timeout = TimeSpan.FromSeconds(Math.Max(5, device.ShutdownTimeoutSeconds));
            var result = await session.ExecuteAsync(ShutdownCommand, timeout, ct);

            if (result.ExitCode is null or 0)
            {
                return AdapterResult.Ok("WINRM_SHUTDOWN_OK", dryRun: false,
                    $"Comando «{ShutdownCommand}» inviato con successo.", DeviceState.ShuttingDown);
            }

            logger.LogWarning("Comando di spegnimento WinRM su «{Device}» terminato con uscita {Exit}: {Error}",
                device.Name, result.ExitCode, result.Stderr);
            return AdapterResult.Fail("WINRM_COMMAND_FAILED", dryRun: false,
                $"Il comando è stato eseguito ma ha restituito l'uscita {result.ExitCode}: {result.Stderr}");
        }
        catch (WinRmCertificateRejectedException)
        {
            return AdapterResult.Fail("WINRM_CERT_MISMATCH", dryRun: false,
                "Il certificato TLS presentato dall'host non corrisponde a quello registrato: " +
                "possibile intercettazione o host sostituito. Connessione rifiutata.");
        }
        catch (WinRmAuthenticationException)
        {
            return AdapterResult.Fail("WINRM_AUTH_FAILED", dryRun: false,
                "Autenticazione WinRM non riuscita: verificare credenziali.");
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            return AdapterResult.Fail("WINRM_UNREACHABLE", dryRun: false,
                $"Dispositivo non raggiungibile via WinRM: {ex.Message}");
        }
    }

    /// <summary>WinRM non supporta l'accensione: non ha senso provare a connettersi a un host spento.</summary>
    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Fail("WINRM_CANNOT_START", dryRun: false,
            "WinRM non può accendere un dispositivo spento: usare Wake-on-LAN, Redfish o BMC."));

    public async Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var prep = Prepare(device, requirePinnedCertificate: true);
        if (prep.Failure is not null) return prep.Failure;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), prep.Credential!, prep.CertificatePolicy!,
                TimeSpan.FromSeconds(10), ct);

            // Comando innocuo: la connessione riuscita è già la prova che il sistema risponde.
            await session.ExecuteAsync(ProbeCommand, TimeSpan.FromSeconds(10), ct);

            return AdapterResult.Ok("WINRM_CHECK_ON", dryRun: false,
                "Connessione WinRM riuscita: il sistema risponde.", DeviceState.On);
        }
        catch (WinRmCertificateRejectedException)
        {
            return AdapterResult.Ok("WINRM_CHECK_CERT_MISMATCH", dryRun: false,
                "Certificato TLS non corrispondente: stato non verificabile in sicurezza.", DeviceState.Unknown);
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            // Non raggiungibile non è prova di arresto (§10): resta Unreachable, non Off.
            return AdapterResult.Ok("WINRM_CHECK_UNREACHABLE", dryRun: false,
                $"WinRM non raggiungibile: {ex.Message}", DeviceState.Unreachable);
        }
    }

    public async Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return AdapterResult.Fail("WINRM_NO_CREDENTIALS", dryRun: false,
                "Nessun profilo di connessione valido associato al dispositivo.");
        }

        var pinned = device.ConnectionProfile?.Fingerprint;
        string? observed = null;

        try
        {
            await using var session = await sessionFactory.ConnectAsync(device.Hostname,
                ResolvePort(device), credential,
                certificateThumbprintPolicy: thumbprint =>
                {
                    observed = thumbprint;
                    // Trust-on-first-use riservato esclusivamente al test: se un thumbprint è già
                    // pinnato viene verificato rigorosamente; se non lo è, si accetta solo per
                    // osservarlo e riportarlo — l'amministratore deve ancora confermarlo
                    // esplicitamente prima che possa essere usato per un'azione reale (§8.4).
                    return string.IsNullOrWhiteSpace(pinned)
                        || string.Equals(pinned, thumbprint, StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10), ct);

            await session.ExecuteAsync(ProbeCommand, TimeSpan.FromSeconds(10), ct);

            if (string.IsNullOrWhiteSpace(pinned))
            {
                return AdapterResult.Ok("WINRM_TEST_CERT_OBSERVED", dryRun: false,
                    $"Connessione riuscita. Thumbprint del certificato osservato: {observed}. " +
                    "Non è ancora registrato: nessuna azione reale sarà possibile finché non " +
                    "viene salvato esplicitamente nel profilo di connessione.", DeviceState.On);
            }

            return AdapterResult.Ok("WINRM_TEST_OK", dryRun: false,
                "Connessione riuscita, certificato TLS verificato.", DeviceState.On);
        }
        catch (WinRmCertificateRejectedException)
        {
            return AdapterResult.Fail("WINRM_TEST_CERT_MISMATCH", dryRun: false,
                $"Il thumbprint osservato ({observed}) non corrisponde a quello registrato " +
                $"({pinned}). Connessione rifiutata: possibile host sostituito.");
        }
        catch (WinRmAuthenticationException)
        {
            return AdapterResult.Fail("WINRM_TEST_AUTH_FAILED", dryRun: false,
                "Connessione TLS stabilita ma autenticazione non riuscita.");
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            return AdapterResult.Fail("WINRM_TEST_UNREACHABLE", dryRun: false,
                $"Dispositivo non raggiungibile: {ex.Message}");
        }
    }

    private sealed record Preparation
    {
        public ResolvedCredential? Credential { get; init; }
        public Func<string, bool>? CertificatePolicy { get; init; }
        public AdapterResult? Failure { get; init; }
    }

    private Preparation Prepare(ManagedDevice device, bool requirePinnedCertificate)
    {
        var credential = CredentialResolver.Resolve(device, credentialProtector);
        if (credential is null)
        {
            return new Preparation
            {
                Failure = AdapterResult.Fail("WINRM_NO_CREDENTIALS", dryRun: false,
                    "Nessun profilo di connessione valido o segreto non decifrabile.")
            };
        }

        var pinned = device.ConnectionProfile?.Fingerprint;
        if (requirePinnedCertificate && string.IsNullOrWhiteSpace(pinned))
        {
            // Nessuna connessione reale senza un thumbprint TLS registrato: applicazione del
            // divieto di StrictHostKeyChecking=no anche per WinRM (§8.4). Usare "Test
            // connessione" per osservarlo e registrarlo esplicitamente prima di procedere.
            return new Preparation
            {
                Failure = AdapterResult.Fail("WINRM_CERT_NOT_PINNED", dryRun: false,
                    "Nessun thumbprint del certificato TLS registrato per questo profilo: " +
                    "eseguire «Test connessione» e salvarlo esplicitamente prima di procedere.")
            };
        }

        return new Preparation
        {
            Credential = credential,
            CertificatePolicy = thumbprint => string.Equals(pinned, thumbprint, StringComparison.Ordinal)
        };
    }

    private int ResolvePort(ManagedDevice device) => device.ManagementPort ?? DefaultWinRmHttpsPort;

    private static bool IsConnectivityException(Exception ex) =>
        ex is System.Net.Sockets.SocketException or HttpRequestException or TaskCanceledException
            or TimeoutException or InvalidOperationException;
}
