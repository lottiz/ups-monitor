using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters.Live.WinRm;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Verifica <see cref="WinRmShutdownAdapter"/> contro una sessione WinRM programmabile, in
/// assenza di un host Windows reale in questo ambiente di sviluppo (§21). Copre le decisioni
/// che l'adapter prende da solo: comando fisso inviato, gestione del pinning del certificato TLS,
/// interpretazione degli esiti — non il protocollo WS-Management stesso, che non è stato
/// validato contro un host reale (vedi nota in WinRmHttpSessionFactory).
/// </summary>
public sealed class WinRmShutdownAdapterTests
{
    private static ManagedDevice Device(ShutdownProtocol protocol = ShutdownProtocol.WinRm,
        string? fingerprint = "fake-thumbprint-sha256", byte[]? secret = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "win-test",
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

    private static WinRmShutdownAdapter CreateAdapter(FakeWinRmSessionFactory factory,
        FakeCredentialProtector? protector = null) =>
        new(factory, protector ?? new FakeCredentialProtector(), NullLogger<WinRmShutdownAdapter>.Instance);

    [Fact]
    public async Task Lo_spegnimento_invia_il_comando_fisso_configurato()
    {
        var factory = new FakeWinRmSessionFactory();
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.True(result.Success);
        Assert.False(result.WasDryRun);
        Assert.Equal(DeviceState.ShuttingDown, result.ObservedState);
        Assert.Single(factory.ExecutedCommands);
        Assert.Equal("shutdown /s /f /t 0", factory.ExecutedCommands[0].Command);
    }

    [Fact]
    public async Task Senza_thumbprint_pinnato_lo_spegnimento_si_rifiuta_senza_connettersi()
    {
        var factory = new FakeWinRmSessionFactory();
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: null);

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("WINRM_CERT_NOT_PINNED", result.ResultCode);
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task Un_thumbprint_non_corrispondente_rifiuta_la_connessione()
    {
        var factory = new FakeWinRmSessionFactory { ThumbprintToPresent = "diverso-da-quello-pinnato" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: "thumbprint-pinnato");

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("WINRM_CERT_MISMATCH", result.ResultCode);
    }

    [Fact]
    public async Task Senza_credenziali_decifrabili_l_azione_si_rifiuta()
    {
        var factory = new FakeWinRmSessionFactory();
        var protector = new FakeCredentialProtector { FailToUnprotect = true };
        var adapter = CreateAdapter(factory, protector);

        var result = await adapter.ShutdownAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("WINRM_NO_CREDENTIALS", result.ResultCode);
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task Un_uscita_diversa_da_zero_e_un_fallimento()
    {
        var factory = new FakeWinRmSessionFactory
        {
            NextResult = new WinRmCommandResult(1, "", "permesso negato"),
        };
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("WINRM_COMMAND_FAILED", result.ResultCode);
    }

    [Fact]
    public async Task L_autenticazione_fallita_e_distinta_dall_irraggiungibilita()
    {
        var factory = new FakeWinRmSessionFactory { ThrowAuthenticationException = true };
        var adapter = CreateAdapter(factory);

        var result = await adapter.ShutdownAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("WINRM_AUTH_FAILED", result.ResultCode);
    }

    [Fact]
    public async Task L_adapter_WinRM_non_puo_mai_accendere()
    {
        var factory = new FakeWinRmSessionFactory();
        var adapter = CreateAdapter(factory);

        var result = await adapter.StartupAsync(Device());

        Assert.False(result.Success);
        Assert.Equal("WINRM_CANNOT_START", result.ResultCode);
        Assert.Empty(factory.ExecutedCommands);
    }

    [Fact]
    public async Task Un_dispositivo_non_raggiungibile_alla_verifica_non_e_prova_di_arresto()
    {
        var factory = new FakeWinRmSessionFactory { ThrowConnectivityException = true };
        var adapter = CreateAdapter(factory);

        var result = await adapter.CheckStateAsync(Device());

        Assert.True(result.Success);
        Assert.Equal(DeviceState.Unreachable, result.ObservedState);
    }

    [Fact]
    public async Task TestConnection_senza_thumbprint_pinnato_lo_osserva_e_lo_riporta_senza_azioni()
    {
        var factory = new FakeWinRmSessionFactory { ThumbprintToPresent = "nuovo-thumbprint" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: null);

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("WINRM_TEST_CERT_OBSERVED", result.ResultCode);
        Assert.Contains("nuovo-thumbprint", result.Message);
        Assert.Contains("Non è ancora registrato", result.Message);
    }

    [Fact]
    public async Task TestConnection_con_thumbprint_pinnato_e_corrispondente_ha_successo()
    {
        var factory = new FakeWinRmSessionFactory { ThumbprintToPresent = "thumb-corretto" };
        var adapter = CreateAdapter(factory);
        var device = Device(fingerprint: "thumb-corretto");

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("WINRM_TEST_OK", result.ResultCode);
    }

    [Fact]
    public void L_adapter_dichiara_di_eseguire_operazioni_reali_ma_non_puo_avviare()
    {
        UPSControlCenter.Core.Abstractions.IDeviceAdapter adapter = CreateAdapter(new FakeWinRmSessionFactory());
        Assert.True(adapter.PerformsRealOperations);
        Assert.Equal(ShutdownProtocol.WinRm, adapter.SupportedShutdownProtocol);
        Assert.Null(adapter.SupportedStartupProtocol);
    }

    [Fact]
    public void CanHandle_riconosce_il_protocollo_di_spegnimento_o_di_verifica_stato()
    {
        var adapter = CreateAdapter(new FakeWinRmSessionFactory());

        Assert.True(adapter.CanHandle(Device(ShutdownProtocol.WinRm)));
        Assert.False(adapter.CanHandle(Device(ShutdownProtocol.Ssh)));

        var checkOnlyDevice = Device(ShutdownProtocol.Ssh);
        checkOnlyDevice.StatusCheckMethod = StatusCheckMethod.WinRm;
        Assert.True(adapter.CanHandle(checkOnlyDevice));
    }
}
