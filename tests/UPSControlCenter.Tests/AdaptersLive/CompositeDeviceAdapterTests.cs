using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters;
using UPSControlCenter.Adapters.Live;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using Xunit;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Un adapter reale fittizio che dichiara di supportare un singolo protocollo e registra se
/// è stato invocato, senza eseguire alcuna operazione reale: qui si verifica solo la logica di
/// instradamento/abilitazione di <see cref="CompositeDeviceAdapter"/>, non un vero protocollo.
/// </summary>
internal sealed class StubRealAdapter(
    string name, ShutdownProtocol? shutdownProtocol = null, StartupProtocol? startupProtocol = null)
    : IDeviceAdapter
{
    public bool Invoked { get; private set; }
    public string AdapterName => name;
    public bool PerformsRealOperations => true;
    public ShutdownProtocol? SupportedShutdownProtocol => shutdownProtocol;
    public StartupProtocol? SupportedStartupProtocol => startupProtocol;
    public bool CanHandle(ManagedDevice device) => true;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Invoked = true;
        return Task.FromResult(AdapterResult.Ok("STUB_SHUTDOWN", dryRun: false, "ok",
            DeviceState.ShuttingDown));
    }

    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Invoked = true;
        return Task.FromResult(AdapterResult.Ok("STUB_STARTUP", dryRun: false, "ok",
            DeviceState.Starting));
    }

    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Invoked = true;
        return Task.FromResult(AdapterResult.Ok("STUB_CHECK", dryRun: false, "ok", DeviceState.On));
    }

    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Invoked = true;
        return Task.FromResult(AdapterResult.Ok("STUB_TEST", dryRun: false, "ok", DeviceState.Unknown));
    }
}

public class CompositeDeviceAdapterTests
{
    private static ManagedDevice BuildDevice(
        ShutdownProtocol shutdownProtocol = ShutdownProtocol.Ssh,
        StartupProtocol startupProtocol = StartupProtocol.WakeOnLan,
        StatusCheckMethod statusCheckMethod = StatusCheckMethod.None) => new()
    {
        Name = "dev-1",
        Hostname = "dev-1.local",
        ShutdownProtocol = shutdownProtocol,
        StartupProtocol = startupProtocol,
        StatusCheckMethod = statusCheckMethod,
    };

    private static (CompositeDeviceAdapter Composite, StubRealAdapter Ssh, DryRunDeviceAdapter DryRun,
        ExecutionModeOptions Options) Build(IList<string>? liveEnabledAdapters = null)
    {
        var sshAdapter = new StubRealAdapter("Ssh", shutdownProtocol: ShutdownProtocol.Ssh);
        var dryRun = new DryRunDeviceAdapter(NullLogger<DryRunDeviceAdapter>.Instance);
        var options = new ExecutionModeOptions { LiveEnabledAdapters = liveEnabledAdapters ?? [] };
        var composite = new CompositeDeviceAdapter(
            [sshAdapter], options, dryRun, NullLogger<CompositeDeviceAdapter>.Instance);
        return (composite, sshAdapter, dryRun, options);
    }

    [Fact]
    public async Task Protocollo_abilitato_in_LIVE_viene_instradato_all_adapter_reale()
    {
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: ["Ssh"]);
        var device = BuildDevice();

        var result = await composite.ShutdownAsync(device);

        Assert.True(ssh.Invoked);
        Assert.Equal("STUB_SHUTDOWN", result.ResultCode);
    }

    [Fact]
    public async Task Protocollo_non_abilitato_in_LIVE_ricade_sul_simulato_senza_invocare_il_reale()
    {
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: []);
        var device = BuildDevice();

        var result = await composite.ShutdownAsync(device);

        Assert.False(ssh.Invoked);
        Assert.Equal("DRY_RUN_SHUTDOWN", result.ResultCode);
    }

    [Fact]
    public async Task Nessun_adapter_reale_per_il_protocollo_richiesto_ricade_sul_simulato()
    {
        // Il protocollo QnapSsh non ha alcun adapter reale registrato in questo setup.
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: ["Ssh", "QnapSsh"]);
        var device = BuildDevice(shutdownProtocol: ShutdownProtocol.QnapSsh);

        var result = await composite.ShutdownAsync(device);

        Assert.False(ssh.Invoked);
        Assert.Equal("DRY_RUN_SHUTDOWN", result.ResultCode);
    }

    [Fact]
    public async Task Abilitazione_letta_a_ogni_chiamata_non_catturata_alla_costruzione()
    {
        // Simula l'effetto di ExecutionModeSyncService che aggiorna le opzioni a runtime: la
        // stessa istanza di CompositeDeviceAdapter deve riflettere il cambiamento immediatamente,
        // senza bisogno di essere ricreata.
        var (composite, ssh, _, options) = Build(liveEnabledAdapters: []);
        var device = BuildDevice();

        var before = await composite.ShutdownAsync(device);
        Assert.False(ssh.Invoked);
        Assert.Equal("DRY_RUN_SHUTDOWN", before.ResultCode);

        options.LiveEnabledAdapters = ["Ssh"];

        var after = await composite.ShutdownAsync(device);
        Assert.True(ssh.Invoked);
        Assert.Equal("STUB_SHUTDOWN", after.ResultCode);
    }

    [Fact]
    public async Task Startup_e_shutdown_dispatchano_su_protocolli_indipendenti()
    {
        // Un dispositivo può spegnersi via SSH ma accendersi via Wake-on-LAN (§12): in questo
        // setup non è registrato alcun adapter reale per lo startup, quindi deve ricadere sul
        // simulato anche se l'adapter SSH è abilitato e invocato per lo shutdown.
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: ["Ssh", "WakeOnLan"]);
        var device = BuildDevice(shutdownProtocol: ShutdownProtocol.Ssh,
            startupProtocol: StartupProtocol.WakeOnLan);

        var shutdownResult = await composite.ShutdownAsync(device);
        var startupResult = await composite.StartupAsync(device);

        Assert.True(ssh.Invoked);
        Assert.Equal("STUB_SHUTDOWN", shutdownResult.ResultCode);
        Assert.Equal("DRY_RUN_STARTUP", startupResult.ResultCode);
    }

    [Fact]
    public async Task CheckState_con_metodo_non_implementato_resta_onestamente_ignoto()
    {
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: ["Ssh"]);
        var device = BuildDevice(statusCheckMethod: StatusCheckMethod.Ping);

        var result = await composite.CheckStateAsync(device);

        Assert.False(ssh.Invoked);
        Assert.Equal("CHECK_METHOD_NOT_IMPLEMENTED", result.ResultCode);
        Assert.Equal(DeviceState.Unknown, result.ObservedState);
    }

    [Fact]
    public async Task CheckState_con_metodo_Ssh_instradato_segue_StatusCheckMethod_non_ShutdownProtocol()
    {
        // StatusCheckMethod è una preoccupazione indipendente dal protocollo di spegnimento:
        // qui lo spegnimento è QnapSsh (nessun adapter reale) ma la verifica di stato è Ssh
        // (adapter reale presente) — devono comportarsi diversamente.
        var (composite, ssh, _, _) = Build(liveEnabledAdapters: ["Ssh"]);
        var device = BuildDevice(shutdownProtocol: ShutdownProtocol.QnapSsh,
            statusCheckMethod: StatusCheckMethod.Ssh);

        var result = await composite.CheckStateAsync(device);

        Assert.True(ssh.Invoked);
        Assert.Equal("STUB_CHECK", result.ResultCode);
    }
}
