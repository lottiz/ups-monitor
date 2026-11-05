using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Simulator;

namespace UPSControlCenter.Web.Controllers;

/// <summary>
/// Simulatore di blackout (§14).
/// <para>
/// Usa lo stesso motore decisionale dell'esecuzione reale e adapter esclusivamente fittizi.
/// Non esiste un percorso di codice che permetta al simulatore di raggiungere un adapter reale:
/// <see cref="BlackoutSimulator"/> non riceve alcun <c>IDeviceAdapter</c> dal contenitore.
/// </para>
/// </summary>
[Authorize(Policy = Policies.RunSimulation)]
public sealed class SimulatorController(
    UpsDbContext db,
    ILogger<SimulatorController> logger) : Controller
{
    public IActionResult Index() => View();

    /// <summary>Scenari predefiniti corrispondenti ai casi di test di §21.</summary>
    [HttpGet]
    public IActionResult GetPresets() => Json(new[]
    {
        new { name = "Blackout 5 minuti", scenario = ScenarioPresets.ShortBlackout },
        new { name = "Blackout 20 minuti", scenario = ScenarioPresets.MediumBlackout },
        new { name = "Blackout 30 minuti", scenario = ScenarioPresets.ThresholdBlackout },
        new { name = "Autonomia sotto 30 minuti", scenario = ScenarioPresets.LowRuntime },
        new { name = "Autonomia non disponibile", scenario = ScenarioPresets.UnknownRuntime },
        new { name = "Stato LB improvviso", scenario = ScenarioPresets.LowBattery },
        new { name = "Perdita comunicazione NUT", scenario = ScenarioPresets.CommunicationLost },
        new { name = "Ritorno corrente prima della soglia", scenario = ScenarioPresets.PowerRestored },
        new { name = "Secondo blackout", scenario = ScenarioPresets.SecondBlackout },
        new { name = "Crash orchestrator", scenario = ScenarioPresets.OrchestratorCrash }
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<SimulationResultDto>> Run(
        [FromBody] SimulationScenarioDto scenario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var policy = await db.ShutdownPolicies
            .AsNoTracking()
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.IsActive, ct);

        if (policy is null)
            return BadRequest(new
            {
                message = "Nessuna policy di spegnimento attiva: impossibile simulare."
            });

        var (executableSteps, devices) = await LoadSequenceAsync(policy.DefaultSequenceId, ct);

        var simulator = new BlackoutSimulator();
        var result = await simulator.RunAsync(scenario, policy, [.. policy.Rules],
            executableSteps, devices);

        // La simulazione viene archiviata: serve a confrontare l'esito atteso con quello reale
        // dopo un blackout e a documentare le verifiche fatte prima di passare a LIVE.
        db.SimulationRuns.Add(new SimulationRun
        {
            Id = result.Id,
            Name = scenario.Name,
            ScenarioJson = System.Text.Json.JsonSerializer.Serialize(scenario),
            TimelineJson = System.Text.Json.JsonSerializer.Serialize(result.Timeline),
            State = RunState.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            CreatedBy = User.Identity?.Name,
            SimulatedDurationSeconds = result.SimulatedDurationSeconds,
            Summary = result.Summary
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Simulazione «{Scenario}» eseguita da {User}: spegnimento {Triggered}.",
            scenario.Name, User.Identity?.Name,
            result.ShutdownTriggered ? $"al secondo {result.ShutdownTriggeredAtSecond}" : "non avviato");

        return Ok(result);
    }

    /// <summary>Esporta la timeline in CSV per l'archiviazione delle verifiche (§14).</summary>
    [HttpGet]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var run = await db.SimulationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return NotFound();

        var timeline = System.Text.Json.JsonSerializer
            .Deserialize<List<SimulationTimelineEntryDto>>(run.TimelineJson ?? "[]") ?? [];

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Secondo;Timestamp;StatoUPS;AutonomiaResidua;CaricaBatteria;Regola;" +
                       "AzionePrevista;Esito;Motivazione;Errore");

        foreach (var e in timeline)
        {
            // I punti e virgola nei testi liberi vanno neutralizzati per non rompere le colonne.
            csv.AppendLine(string.Join(';',
                e.SimulatedSecond,
                e.Timestamp.ToString("O"),
                e.UpsStatus,
                e.RemainingRuntimeSeconds?.ToString() ?? "n.d.",
                e.BatteryChargePercent?.ToString("F1") ?? "n.d.",
                Escape(e.AppliedRule),
                Escape(e.PlannedAction),
                Escape(e.SimulatedOutcome),
                Escape(e.DecisionReason),
                Escape(e.Error)));
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
            $"simulazione-{run.Name}-{run.StartedAt:yyyyMMdd-HHmmss}.csv");

        static string Escape(string? value) =>
            string.IsNullOrEmpty(value) ? "" : value.Replace(';', ',').Replace('\n', ' ');
    }

    /// <summary>
    /// Carica la sequenza di spegnimento predefinita e risolve gruppi ed esclusioni, nello
    /// stesso modo in cui lo farebbe l'esecuzione reale (§14): la simulazione deve vedere
    /// esattamente gli step che vedrebbe l'Orchestrator.
    /// </summary>
    private async Task<(IReadOnlyList<ExecutableStep> Steps, IReadOnlyDictionary<Guid, ManagedDevice> Devices)>
        LoadSequenceAsync(Guid? sequenceId, CancellationToken ct)
    {
        if (sequenceId is null) return ([], new Dictionary<Guid, ManagedDevice>());

        var steps = await db.ShutdownSteps
            .AsNoTracking()
            .Where(s => s.ShutdownSequenceId == sequenceId)
            .ToListAsync(ct);

        var groupedDevices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => d.DeviceGroupId != null)
            .Select(d => new { d.Id, d.DeviceGroupId })
            .ToListAsync(ct);

        var groupMembers = groupedDevices
            .GroupBy(d => d.DeviceGroupId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(d => d.Id).ToList());

        var executable = SequenceStepResolver.Resolve(steps, groupMembers);

        var deviceIds = executable.SelectMany(s => s.TargetDeviceIds).Distinct().ToList();
        var devices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        return (executable, devices);
    }
}

/// <summary>Scenari predefiniti, allineati ai test automatici di §21.</summary>
internal static class ScenarioPresets
{
    public static SimulationScenarioDto ShortBlackout => new()
    {
        Name = "Blackout 5 minuti",
        BlackoutDurationSeconds = 300,
        InitialRuntimeSeconds = 2400,
        PowerRestoredAtSecond = 300
    };

    public static SimulationScenarioDto MediumBlackout => new()
    {
        Name = "Blackout 20 minuti",
        BlackoutDurationSeconds = 1200,
        InitialRuntimeSeconds = 3600,
        RuntimeDecayPerSecond = 1.0m,
        PowerRestoredAtSecond = 1200
    };

    public static SimulationScenarioDto ThresholdBlackout => new()
    {
        Name = "Blackout 30 minuti",
        BlackoutDurationSeconds = 2400,
        InitialRuntimeSeconds = 7200,
        RuntimeDecayPerSecond = 0.5m
    };

    public static SimulationScenarioDto LowRuntime => new()
    {
        Name = "Autonomia sotto 30 minuti",
        BlackoutDurationSeconds = 3600,
        InitialRuntimeSeconds = 2100,
        RuntimeDecayPerSecond = 1.5m
    };

    /// <summary>UPS che non fornisce <c>battery.runtime</c>: verifica la UnknownRuntimePolicy.</summary>
    public static SimulationScenarioDto UnknownRuntime => new()
    {
        Name = "Autonomia non disponibile",
        BlackoutDurationSeconds = 3600,
        InitialRuntimeSeconds = null,
        InitialBatteryChargePercent = 80m
    };

    public static SimulationScenarioDto LowBattery => new()
    {
        Name = "Stato LB improvviso",
        BlackoutDurationSeconds = 3600,
        InitialRuntimeSeconds = 3600,
        LowBatteryAtSecond = 120
    };

    public static SimulationScenarioDto CommunicationLost => new()
    {
        Name = "Perdita comunicazione NUT",
        BlackoutDurationSeconds = 3600,
        InitialRuntimeSeconds = 2400,
        CommunicationLostFromSecond = 300,
        CommunicationLostToSecond = 900
    };

    public static SimulationScenarioDto PowerRestored => new()
    {
        Name = "Ritorno corrente prima della soglia",
        BlackoutDurationSeconds = 600,
        InitialRuntimeSeconds = 2400,
        PowerRestoredAtSecond = 600
    };

    public static SimulationScenarioDto SecondBlackout => new()
    {
        Name = "Secondo blackout",
        BlackoutDurationSeconds = 600,
        InitialRuntimeSeconds = 2400,
        PowerRestoredAtSecond = 600,
        SecondBlackoutAtSecond = 1200,
        SecondBlackoutDurationSeconds = 2400
    };

    public static SimulationScenarioDto OrchestratorCrash => new()
    {
        Name = "Crash orchestrator",
        BlackoutDurationSeconds = 3600,
        InitialRuntimeSeconds = 2400,
        OrchestratorCrashAtSecond = 300
    };
}
