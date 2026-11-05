using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Adapters;

namespace UPSControlCenter.Tests.Adapters;

/// <summary>
/// Verifica il vincolo assoluto del progetto: in DRY_RUN nessun comando reale può raggiungere i
/// dispositivi (§15, §21). L'isolamento è verificato sulla <b>composizione</b> del contenitore,
/// non sul comportamento dei singoli metodi, perché è lì che risiede la garanzia.
/// </summary>
public sealed class DryRunIsolationTests
{
    private static ServiceProvider BuildProvider(ExecutionModeOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDeviceAdapters(options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void In_DRY_RUN_nessun_adapter_reale_e_registrato_nel_contenitore()
    {
        using var provider = BuildProvider(new ExecutionModeOptions
        {
            Mode = ExecutionMode.DryRun
        });

        var adapters = provider.GetServices<IDeviceAdapter>().ToList();

        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.False(a.PerformsRealOperations));
    }

    [Fact]
    public void In_DRY_RUN_il_selettore_restituisce_sempre_l_adapter_simulato()
    {
        using var provider = BuildProvider(new ExecutionModeOptions
        {
            Mode = ExecutionMode.DryRun
        });

        var selector = provider.GetRequiredService<IAdapterSelector>();

        // Si provano tutte le tipologie e tutti i protocolli: nessuna combinazione deve
        // produrre un adapter reale.
        foreach (var type in Enum.GetValues<DeviceType>())
        {
            foreach (var protocol in Enum.GetValues<ShutdownProtocol>())
            {
                var device = TestData.Device($"dev-{type}-{protocol}", type);
                device.ShutdownProtocol = protocol;

                var adapter = selector.Select(device);

                Assert.False(adapter.PerformsRealOperations);
            }
        }

        Assert.Equal(ExecutionMode.DryRun, selector.EffectiveMode);
    }

    [Fact]
    public void Modalita_LIVE_senza_autorizzazione_di_deployment_ricade_su_DRY_RUN()
    {
        // Scenario R18: un database ripristinato da un altro ambiente dichiara LIVE, ma la
        // variabile UPS_ALLOW_LIVE non è presente su questo deployment.
        var options = new ExecutionModeOptions
        {
            Mode = ExecutionMode.Live,
            LiveEnabledAdapters = ["Ssh", "WinRm", "Redfish"],
            DeploymentAllowsLive = false
        };

        Assert.Equal(ExecutionMode.DryRun, options.EffectiveMode);

        using var provider = BuildProvider(options);
        var adapters = provider.GetServices<IDeviceAdapter>().ToList();

        Assert.All(adapters, a => Assert.False(a.PerformsRealOperations));
    }

    [Fact]
    public void Modalita_LIVE_autorizzata_non_abilita_adapter_non_elencati()
    {
        // Anche con LIVE autorizzato, finché la Fase 3 non registra adapter reali il
        // comportamento resta simulato: nessuna attivazione implicita.
        var options = new ExecutionModeOptions
        {
            Mode = ExecutionMode.Live,
            LiveEnabledAdapters = [],
            DeploymentAllowsLive = true
        };

        using var provider = BuildProvider(options);
        var selector = provider.GetRequiredService<IAdapterSelector>();

        var adapter = selector.Select(TestData.Device("srv-test"));
        Assert.False(adapter.PerformsRealOperations);
    }

    [Fact]
    public async Task L_adapter_dry_run_dichiara_sempre_di_non_avere_eseguito_operazioni_reali()
    {
        using var provider = BuildProvider(new ExecutionModeOptions());
        var selector = provider.GetRequiredService<IAdapterSelector>();
        var device = TestData.Device("srv-app");
        var adapter = selector.Select(device);

        var shutdown = await adapter.ShutdownAsync(device);
        var startup = await adapter.StartupAsync(device);
        var check = await adapter.CheckStateAsync(device);
        var test = await adapter.TestConnectionAsync(device);

        Assert.True(shutdown.WasDryRun);
        Assert.True(startup.WasDryRun);
        Assert.True(check.WasDryRun);
        Assert.True(test.WasDryRun);
    }

    [Fact]
    public async Task La_verifica_in_DRY_RUN_non_dichiara_stati_che_non_puo_conoscere()
    {
        using var provider = BuildProvider(new ExecutionModeOptions());
        var selector = provider.GetRequiredService<IAdapterSelector>();
        var device = TestData.Device("srv-app");

        var result = await selector.Select(device).CheckStateAsync(device);

        // In DRY_RUN non esiste evidenza reale: restituire "Off" sarebbe un dato inventato.
        Assert.Equal(DeviceState.Unknown, result.ObservedState);
    }

    [Fact]
    public void Il_simulatore_usa_solo_adapter_fittizi()
    {
        var adapter = new Simulator.SimulatedDeviceAdapter(new Contracts.Dto.SimulationScenarioDto());

        Assert.False(adapter.PerformsRealOperations);
    }
}
