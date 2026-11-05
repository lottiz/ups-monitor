using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters.Live.Redfish;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Verifica <see cref="RedfishDeviceAdapter"/> contro un server HTTPS reale con certificato
/// autofirmato generato al volo: richieste, JSON e TLS sono veri, non simulati (§21).
/// </summary>
public sealed class RedfishDeviceAdapterTests : IAsyncLifetime
{
    private FakeRedfishServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeRedfishServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private ManagedDevice Device(string? fingerprint) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "bmc-test",
        Hostname = "127.0.0.1",
        ManagementPort = _server.Port,
        ShutdownProtocol = ShutdownProtocol.Redfish,
        StartupProtocol = StartupProtocol.Redfish,
        ConnectionProfile = new ConnectionProfile
        {
            Username = "ADMIN",
            AuthType = CredentialAuthType.Password,
            SecretCiphertext = System.Text.Encoding.UTF8.GetBytes("password"),
            Fingerprint = fingerprint
        }
    };

    private static RedfishDeviceAdapter CreateAdapter(FakeCredentialProtector? protector = null) =>
        new(protector ?? new FakeCredentialProtector(), NullLogger<RedfishDeviceAdapter>.Instance);

    [Fact]
    public async Task TestConnection_senza_thumbprint_pinnato_lo_osserva_e_lo_riporta()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: null);

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("REDFISH_TEST_CERT_OBSERVED", result.ResultCode);
        Assert.Contains(_server.CertificateThumbprintSha256, result.Message);
    }

    [Fact]
    public async Task TestConnection_con_thumbprint_pinnato_corretto_ha_successo()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.TestConnectionAsync(device);

        Assert.True(result.Success);
        Assert.Equal("REDFISH_TEST_OK", result.ResultCode);
    }

    [Fact]
    public async Task Senza_thumbprint_pinnato_lo_spegnimento_si_rifiuta_senza_connettersi()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: null);

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("REDFISH_CERT_NOT_PINNED", result.ResultCode);
        Assert.Null(_server.LastResetBody);
    }

    [Fact]
    public async Task Un_thumbprint_non_corrispondente_rifiuta_la_connessione_reale()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: "thumbprint-sbagliato-non-corrisponde");

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("REDFISH_RESET_CERT_MISMATCH", result.ResultCode);
    }

    [Fact]
    public async Task Lo_spegnimento_invia_GracefulShutdown_mai_ForceOff()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.ShutdownAsync(device);

        Assert.True(result.Success);
        Assert.Equal(DeviceState.ShuttingDown, result.ObservedState);
        Assert.NotNull(_server.LastResetBody);
        Assert.Contains("GracefulShutdown", _server.LastResetBody);
        Assert.DoesNotContain("ForceOff", _server.LastResetBody);
    }

    [Fact]
    public async Task L_accensione_invia_ResetType_On()
    {
        var adapter = CreateAdapter();
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.StartupAsync(device);

        Assert.True(result.Success);
        Assert.Equal(DeviceState.Starting, result.ObservedState);
        Assert.Contains("\"On\"", _server.LastResetBody);
    }

    [Theory]
    [InlineData("On", DeviceState.On)]
    [InlineData("Off", DeviceState.Off)]
    public async Task CheckState_interpreta_PowerState_dal_BMC_reale(string powerState, DeviceState expected)
    {
        _server.PowerState = powerState;
        var adapter = CreateAdapter();
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.CheckStateAsync(device);

        Assert.Equal(expected, result.ObservedState);
    }

    [Fact]
    public async Task CheckState_con_autenticazione_rifiutata_non_e_prova_di_arresto()
    {
        _server.RejectAuth = true;
        var adapter = CreateAdapter();
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.CheckStateAsync(device);

        // §10: un errore di autenticazione o di rete non prova che il sistema sia spento.
        Assert.Equal(DeviceState.Unreachable, result.ObservedState);
    }

    [Fact]
    public async Task Senza_credenziali_decifrabili_l_azione_si_rifiuta_senza_connettersi()
    {
        var protector = new FakeCredentialProtector { FailToUnprotect = true };
        var adapter = CreateAdapter(protector);
        var device = Device(fingerprint: _server.CertificateThumbprintSha256);

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("REDFISH_NO_CREDENTIALS", result.ResultCode);
        Assert.Null(_server.LastResetBody);
    }

    [Fact]
    public void L_adapter_dichiara_i_protocolli_supportati_per_entrambe_le_direzioni()
    {
        var adapter = CreateAdapter();
        Assert.Equal(ShutdownProtocol.Redfish, adapter.SupportedShutdownProtocol);
        Assert.Equal(StartupProtocol.Redfish, adapter.SupportedStartupProtocol);
        Assert.True(adapter.PerformsRealOperations);
    }
}
