using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Simulator;

namespace UPSControlCenter.Tests.SimulatorTests;

/// <summary>
/// Verifica che il simulatore esegua realmente gli step della sequenza con
/// <see cref="SequenceExecutor"/> e <see cref="SimulatedDeviceAdapter"/>, non solo che registri
/// "sequenza avviata" (§14). Questo è il requisito di Fase 2 lasciato aperto in Fase 1.
/// </summary>
public sealed class BlackoutSimulatorSequenceTests
{
    private static ExecutableStep Step(string name, int order, SequenceStepType type,
        params Guid[] targets) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderIndex = order,
        Name = name,
        StepType = type,
        TargetDeviceIds = targets,
        TimeoutSeconds = 5
    };

    [Fact]
    public async Task Allo_scatto_della_soglia_gli_step_vengono_eseguiti_sull_adapter_simulato()
    {
        var app = TestData.Device("app");
        var db = TestData.Device("db");

        var steps = new[]
        {
            Step("Notifica", 10, SequenceStepType.Notify),
            Step("Spegni app", 20, SequenceStepType.ShutdownDevice, app.Id),
            Step("Spegni db", 30, SequenceStepType.ShutdownDevice, db.Id)
        };
        var devices = new Dictionary<Guid, ManagedDevice> { [app.Id] = app, [db.Id] = db };

        var scenario = new SimulationScenarioDto
        {
            Name = "Test esecuzione",
            BlackoutDurationSeconds = 2400,
            InitialRuntimeSeconds = 7200,
            TickSeconds = 10
        };

        var policy = TestData.Policy30Plus30();
        var simulator = new UPSControlCenter.Simulator.BlackoutSimulator();

        var result = await simulator.RunAsync(scenario, policy, [], steps, devices);

        Assert.True(result.ShutdownTriggered);
        Assert.True(result.AllActionsSimulated);

        // Devono comparire righe di timeline per ciascuno step eseguito, non solo il marcatore
        // "Avvio sequenza di spegnimento".
        Assert.Contains(result.Timeline, e => e.PlannedAction == "Spegni app");
        Assert.Contains(result.Timeline, e => e.PlannedAction == "Spegni db");
        Assert.Contains(result.Timeline, e => e.DeviceName == "app");
        Assert.Contains(result.Timeline, e => e.DeviceName == "db");
        Assert.Contains(result.Timeline, e => e.SimulatedOutcome == "Succeeded");

        Assert.Contains("sequenza terminata con stato Completed", result.TriggerReason);
    }

    [Fact]
    public async Task Un_dispositivo_irraggiungibile_nello_scenario_produce_un_fallimento_in_timeline()
    {
        var broken = TestData.Device("broken");
        var steps = new[] { Step("Spegni broken", 10, SequenceStepType.ShutdownDevice, broken.Id) };
        var devices = new Dictionary<Guid, ManagedDevice> { [broken.Id] = broken };

        var scenario = new SimulationScenarioDto
        {
            Name = "Dispositivo guasto",
            BlackoutDurationSeconds = 2400,
            InitialRuntimeSeconds = 7200,
            TickSeconds = 10,
            // Lo scenario dichiara il dispositivo come non raggiungibile: il simulatore deve
            // riprodurre questo guasto nell'esito, non limitarsi a un successo sempre uguale.
            UnreachableDeviceIds = [broken.Id]
        };

        var policy = TestData.Policy30Plus30();
        var simulator = new UPSControlCenter.Simulator.BlackoutSimulator();

        var result = await simulator.RunAsync(scenario, policy, [], steps, devices);

        Assert.Contains(result.Timeline, e => e.SimulatedOutcome == "Failed" && e.DeviceName == "broken");
        Assert.Contains("sequenza terminata con stato Failed", result.TriggerReason);
    }

    [Fact]
    public async Task Senza_una_sequenza_configurata_la_soglia_scatta_senza_eseguire_step()
    {
        var scenario = new SimulationScenarioDto
        {
            Name = "Nessuna sequenza",
            BlackoutDurationSeconds = 2400,
            InitialRuntimeSeconds = 7200,
            TickSeconds = 10
        };

        var policy = TestData.Policy30Plus30();
        var simulator = new UPSControlCenter.Simulator.BlackoutSimulator();

        // Nessuno step passato: rappresenta una policy senza sequenza predefinita associata.
        var result = await simulator.RunAsync(scenario, policy, []);

        Assert.True(result.ShutdownTriggered);
        Assert.Contains(result.Timeline, e => e.PlannedAction == "Avvio sequenza di spegnimento");
        Assert.DoesNotContain(result.Timeline, e => e.DeviceName != null);
    }

    [Fact]
    public async Task Il_simulatore_non_usa_mai_un_adapter_che_dichiari_operazioni_reali()
    {
        var device = TestData.Device("qualunque");
        var steps = new[] { Step("Spegni", 10, SequenceStepType.ShutdownDevice, device.Id) };
        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var selector = new SimulatedAdapterSelector(new SimulationScenarioDto());
        var adapter = selector.Select(device);

        // Garanzia strutturale, non comportamentale: l'adapter restituito non può eseguire
        // operazioni reali, a prescindere da qualunque parametro (§14).
        Assert.False(adapter.PerformsRealOperations);
        Assert.IsType<SimulatedDeviceAdapter>(adapter);
    }
}
