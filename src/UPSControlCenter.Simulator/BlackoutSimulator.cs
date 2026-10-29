using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Motore di simulazione dei blackout (§14).
/// <para>
/// Usa <see cref="RuleEvaluator"/> e <see cref="BlackoutTracker"/>, cioè <b>gli stessi tipi</b>
/// dell'esecuzione reale. Non esiste un motore decisionale alternativo per la simulazione, quindi
/// i risultati non possono divergere da quelli di un blackout vero.
/// </para>
/// </summary>
public sealed class BlackoutSimulator
{
    private readonly RuleEvaluator _evaluator = new();

    public SimulationResultDto Run(SimulationScenarioDto scenario, ShutdownPolicy policy,
        IReadOnlyList<ShutdownRule> rules)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new SimulatedClock(start);
        const string bootId = "simulated-boot";

        var nut = new SimulatedNutClient(scenario, "simulated-ups");
        var tracker = new BlackoutTracker("simulated-ups", clock, bootId, policy.DebounceSamples);

        var timeline = new List<SimulationTimelineEntryDto>();
        var tick = Math.Max(1, scenario.TickSeconds);
        var total = ComputeTotalDuration(scenario);

        var shutdownTriggered = false;
        int? triggeredAt = null;
        string? triggerReason = null;
        var orchestratorAlive = true;

        for (var second = 0; second <= total; second += tick)
        {
            // Crash simulato dell'orchestrator: da qui il suo heartbeat invecchia e il Safety
            // Agent diventa candidato al takeover (§14).
            if (scenario.OrchestratorCrashAtSecond is { } crash && second >= crash)
                orchestratorAlive = false;

            var snapshot = nut.Build(second, clock.UtcNow);
            var outcome = tracker.Process(snapshot);

            var ctx = new EvaluationContext
            {
                Snapshot = snapshot,
                Policy = policy,
                Blackout = outcome.Blackout,
                DebouncedStatus = outcome.DebouncedStatus,
                NowUtc = clock.UtcNow,
                NowMonotonicMs = clock.MonotonicMilliseconds,
                BootId = bootId,
                OrchestratorHeartbeatAge = orchestratorAlive
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(second - (scenario.OrchestratorCrashAtSecond ?? 0)),
                AutomationDisabled = false
            };

            var decision = _evaluator.Evaluate(ctx, rules);

            // La sequenza si avvia una sola volta: dopo il primo scatto si continua a registrare
            // la timeline ma non si ri-decide, come nell'esecuzione reale.
            var plannedAction = (string?)null;
            if (decision.ShouldTriggerShutdown && !shutdownTriggered)
            {
                shutdownTriggered = true;
                triggeredAt = second;
                triggerReason = decision.Reason;
                plannedAction = "Avvio sequenza di spegnimento";
            }
            else if (shutdownTriggered)
            {
                plannedAction = "Sequenza già avviata";
            }

            timeline.Add(new SimulationTimelineEntryDto
            {
                SimulatedSecond = second,
                Timestamp = clock.UtcNow,
                UpsStatus = snapshot.CommunicationOk
                    ? outcome.DebouncedStatus.ToNutString()
                    : "COMM_LOST",
                RemainingRuntimeSeconds = snapshot.BatteryRuntimeSeconds,
                BatteryChargePercent = snapshot.BatteryChargePercent,
                AppliedRule = decision.TriggeredRuleName,
                PlannedAction = plannedAction,
                DecisionReason = decision.Reason,
                SimulatedOutcome = decision.ShouldTriggerShutdown ? "Soglia raggiunta" : "Nessuna azione",
                Events = outcome.Events,
                Error = decision.BasedOnUncertainData
                    ? "Decisione basata su dati incerti o incompleti"
                    : null
            });

            clock.Advance(TimeSpan.FromSeconds(tick));
        }

        return new SimulationResultDto
        {
            Id = Guid.CreateVersion7(),
            ScenarioName = scenario.Name,
            ShutdownTriggered = shutdownTriggered,
            ShutdownTriggeredAtSecond = triggeredAt,
            TriggerReason = triggerReason,
            SimulatedDurationSeconds = total,
            Timeline = timeline,
            AllActionsSimulated = true,
            Summary = BuildSummary(scenario, shutdownTriggered, triggeredAt, triggerReason)
        };
    }

    /// <summary>Durata complessiva da simulare, estesa per coprire ritorno corrente e secondo blackout.</summary>
    private static int ComputeTotalDuration(SimulationScenarioDto scenario)
    {
        var candidates = new List<int> { scenario.BlackoutDurationSeconds };

        if (scenario.PowerRestoredAtSecond is { } r) candidates.Add(r);
        if (scenario.CommunicationLostToSecond is { } c) candidates.Add(c);
        if (scenario.LowBatteryAtSecond is { } lb) candidates.Add(lb);
        if (scenario.SecondBlackoutAtSecond is { } sb)
            candidates.Add(sb + (scenario.SecondBlackoutDurationSeconds ?? 0));

        // Margine di 10 minuti per osservare il comportamento dopo l'ultimo evento.
        return candidates.Max() + 600;
    }

    private static string BuildSummary(SimulationScenarioDto scenario, bool triggered,
        int? at, string? reason)
    {
        if (!triggered)
            return $"Scenario «{scenario.Name}»: nessuna sequenza di spegnimento avviata. " +
                   "Nessuna soglia è stata raggiunta nell'intervallo simulato.";

        return $"Scenario «{scenario.Name}»: sequenza di spegnimento avviata al secondo {at} " +
               $"({TimeSpan.FromSeconds(at ?? 0):hh\\:mm\\:ss}). Motivo: {reason}";
    }
}
