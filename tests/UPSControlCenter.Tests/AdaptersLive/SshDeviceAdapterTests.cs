using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters.Live.Ssh;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Verifica <see cref="SshDeviceAdapter"/> contro una sessione SSH programmabile, in assenza
/// di un server SSH reale in questo ambiente di sviluppo (§21). Copre esattamente le decisioni
/// che l'adapter prende da solo: comando inviato, gestione del fingerprint, interpretazione
/// degli esiti — non il protocollo SSH stesso, responsabilità di SSH.NET.
/// </summary>
public sealed class SshDeviceAdapterTests
{
    private static ManagedDevice Device(ShutdownProtocol protocol = ShutdownProtocol.Ssh,
        string? fingerprint = "fake-fingerprint-sha256", byte[]? secret = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "srv-test",
        Hostname = "127.0.0.1",
        ShutdownProtocol = protocol,
        ShutdownTimeoutSeconds = 60,
        ConnectionProfile = new ConnectionProfile
        {
            Username = "automationuser",
            AuthType = CredentialAuthType.Password,
            SecretCiphertext = secret ?? System.Text.Encoding.UTF8.GetBytes("s3cret"),
            Fingerprint = fingerprint
        }
    };

    private static SshDeviceAdapter CreateAdapter(FakeSshSessionFactory factory,
        SshShutdownProfile? profile = null, FakeCredentialProtector? protector = null) =>
        new(profile ?? SshShutdownProfile.Linux, factory, protector ?? new FakeCredentialProtector(),
            NullLogger<SshDeviceAdapter>.Instance);

    [Fact]
    public async Task Lo_spegnimento_invia_il_comando_Linux_configurato()
    {
        var factory = new FakeSshSessionFactory();
        var adapter = CreateAdapter(factory);
        var device = Device();

        var result = await adapter.ShutdownAsync(device);

        Assert.True(result.Success);
        Assert.False(result.WasDryRun);
        Assert.Equal(DeviceState.ShuttingDown, result.ObservedState);
        Assert.Single(factory.ExecutedCommands);
        Assert.Equal("sudo /sbin/shutdown -h now", factory.ExecutedCommands[0].Command);
    }

    [Fact]
    public async Task Il_profilo_QNAP_invia_poweroff_e_non_il_comando_Linux()
    {
        var factory = new FakeSshSessionFactory();
        var adapter = CreateAdapter(factory, SshShutdownProfile.Qnap);
        var device = Device(ShutdownProtocol.QnapSsh);

        await adapter.ShutdownAsync(device);

        Assert.Equal("poweroff", factory.ExecutedCommands[0].Command);
    }

    [Fact]
    public async Task Senza_fingerprint_pinnato_lo_spegnimento_si_rifiuta_senza_connettersi()
    {
        var factory = new FakeSshSessionFactory();
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: null);

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("SSH_FINGERPRINT_NOT_PINNED", result.ResultCode);
        // Nessun comando deve essere stato inviato: il rifiuto avviene prima di qualunque connessione.
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task Un_fingerprint_non_corrispondente_rifiuta_la_connessione()
    {
        var factory = new FakeSshSessionFactory { FingerprintToPresent = "diverso-da-quello-pinnato" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: "fingerprint-pinnato");

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("SSH_HOST_KEY_MISMATCH", result.ResultCode);
    }

    [Fact]
    public async Task Senza_credenziali_decifrabili_l_azione_si_rifiuta()
    {
        var factory = new FakeSshSessionFactory();
        var protector = new FakeCredentialProtector { FailToUnprotect = true };
        var adapter = CreateAdapter(factory, protector: protector);
        var device = Device();

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("SSH_NO_CREDENTIALS", result.ResultCode);
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task La_disconnessione_durante_il_comando_di_spegnimento_e_un_successo()
    {
        // È l'esito atteso quando il comando ha davvero avviato l'arresto (§10).
        var factory = new FakeSshSessionFactory
        {
            NextResult = new() { DisconnectedDuringExecution = true }
        };
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.True(result.Success);
        Assert.Equal("SSH_SHUTDOWN_DISCONNECTED", result.ResultCode);
        Assert.Contains("atteso", result.Message);
    }

    [Fact]
    public async Task Un_uscita_diversa_da_zero_e_un_fallimento()
    {
        var factory = new FakeSshSessionFactory
        {
            NextResult = new() { ExitStatus = 1, Error = "permesso negato" }
        };
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("SSH_COMMAND_FAILED", result.ResultCode);
    }

    [Fact]
    public async Task L_autenticazione_fallita_e_distinta_dall_irraggiungibilita()
    {
        var factory = new FakeSshSessionFactory { ThrowAuthenticationException = true };
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("SSH_AUTH_FAILED", result.ResultCode);
    }

    [Fact]
    public async Task L_adapter_SSH_non_puo_mai_accendere()
    {
        var factory = new FakeSshSessionFactory();
        var adapter = CreateAdapter(factory);

        var result = await adapter.StartupAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("SSH_CANNOT_START", result.ResultCode);
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task Un_dispositivo_non_raggiungibile_alla_verifica_non_e_prova_di_arresto()
    {
        var factory = new FakeSshSessionFactory { ThrowConnectivityException = true };
        var adapter = CreateAdapter(factory);

        var result = await adapter.CheckStateAsync(Device());

        // Richiesta §10: non raggiungibile ≠ Off.
        Assert.True(result.Success);
        Assert.Equal(DeviceState.Unreachable, result.ObservedState);
    }

    [Fact]
    public async Task TestConnection_senza_fingerprint_pinnato_lo_osserva_e_lo_riporta_senza_azioni()
    {
        var factory = new FakeSshSessionFactory { FingerprintToPresent = "nuovo-fingerprint" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: null);

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("SSH_TEST_FINGERPRINT_OBSERVED", result.ResultCode);
        Assert.Contains("nuovo-fingerprint", result.Message);
        Assert.Contains("Non è ancora registrato", result.Message);
    }

    [Fact]
    public async Task TestConnection_con_fingerprint_pinnato_e_corrispondente_ha_successo()
    {
        var factory = new FakeSshSessionFactory { FingerprintToPresent = "fp-corretto" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: "fp-corretto");

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("SSH_TEST_OK", result.ResultCode);
    }

    [Fact]
    public void L_adapter_dichiara_di_eseguire_operazioni_reali()
    {
        var adapter = CreateAdapter(new FakeSshSessionFactory());
        Assert.True(adapter.PerformsRealOperations);
        Assert.Equal(ShutdownProtocol.Ssh, adapter.SupportedShutdownProtocol);
    }

    [Fact]
    public void CanHandle_distingue_Ssh_da_QnapSsh()
    {
        var linuxAdapter = CreateAdapter(new FakeSshSessionFactory(), SshShutdownProfile.Linux);
        var qnapAdapter = CreateAdapter(new FakeSshSessionFactory(), SshShutdownProfile.Qnap);

        Assert.True(linuxAdapter.CanHandle(Device(ShutdownProtocol.Ssh)));
        Assert.False(linuxAdapter.CanHandle(Device(ShutdownProtocol.QnapSsh)));
        Assert.True(qnapAdapter.CanHandle(Device(ShutdownProtocol.QnapSsh)));
        Assert.False(qnapAdapter.CanHandle(Device(ShutdownProtocol.Ssh)));
    }
}
