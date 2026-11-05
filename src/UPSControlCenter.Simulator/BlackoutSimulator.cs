using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Motore di simulazione dei blackout (§14).
/// <para>
/// Usa <see cref="RuleEvaluator"/>, <see cref="BlackoutTracker"/> e <see cref="SequenceExecutor"/>,
/// cioè <b>gli stessi tipi</b> dell'esecuzione reale. Non esiste un motore decisionale o un
/// esecutore alternativo per la simulazione: una sequenza simulata e una reale eseguono
/// esattamente lo stesso codice, con <see cref="SimulatedDeviceAdapter"/> al posto
/// dell'adapter reale (§8.1) e senza attese reali sui ritardi configurati.
/// </para>
/// </summary>
public sealed class BlackoutSimulator
{
    private readonly RuleEvaluator _evaluator = new();

    /// <param name="scenario">Parametri dello scenario simulato.</param>
    /// <param name="policy">Policy di spegnimento da valutare.</param>
    /// <param name="rules">Regole della policy.</param>
    /// <param name="shutdownSteps">
    /// Step della sequenza di spegnimento predefinita, già risolti (gruppi espansi, esclusioni
    /// applicate). Vuoto se non è stata configurata una sequenza: la simulazione mostrerà la
    /// soglia raggiunta senza eseguire alcuno step.
    /// </param>
    /// <param name="devices">Dispositivi coinvolti negli step, indicizzati per id.</param>
    public async Task<SimulationResultDto> RunAsync(SimulationScenarioDto scenario,
        ShutdownPolicy policy, IReadOnlyList<ShutdownRule> rules,
        IReadOnlyList<ExecutableStep>? shutdownSteps = null,
        IReadOnlyDictionary<Guid, ManagedDevice>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);

        shutdownSteps ??= [];
        devices ??= new Dictionary<Guid, ManagedDevice>();

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new SimulatedClock(start);
        const string bootId = "simulated-boot";

        var nut = new SimulatedNutClient(scenario, "simulated-ups");
        var tracker = new BlackoutTracker("simulated-ups", clock, bootId, policy.DebounceSamples);
        var executor = new SequenceExecutor(new SimulatedAdapterSelector(scenario), clock);
        var runStore = new InMemorySequenceRunStore();

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

            // L'esecuzione avviene una sola volta, nel tick dello scatto: usa lo stesso motore
            // dell'esecuzione reale, con l'adapter simulato e senza attendere i ritardi
            // configurati (§14). Le righe di timeline prodotte qui mostrano esito per esito,
            // non solo "avviata".
            if (plannedAction == "Avvio sequenza di spegnimento" && shutdownSteps.Count > 0)
            {
                var result = await executor.ExecuteAsync(
                    runId: $"simulation:{triggeredAt}",
                    direction: SequenceDirection.Shutdown,
                    steps: shutdownSteps,
                    devices: devices,
                    store: runStore,
                    evaluateCondition: node => ConditionEvaluator.Evaluate(node, ctx),
                    delay: (_, _) => Task.CompletedTask);

                foreach (var action in result.Actions)
                {
                    var step = shutdownSteps.FirstOrDefault(s => s.Id == action.StepId);
                    var deviceName = action.DeviceId is { } id && devices.TryGetValue(id, out var d)
                        ? d.Name
                        : null;

                    timeline.Add(new SimulationTimelineEntryDto
                    {
                        SimulatedSecond = second,
                        Timestamp = clock.UtcNow,
                        UpsStatus = outcome.DebouncedStatus.ToNutString(),
                        RemainingRuntimeSeconds = snapshot.BatteryRuntimeSeconds,
                        BatteryChargePercent = snapshot.BatteryChargePercent,
                        AppliedRule = decision.TriggeredRuleName,
                        PlannedAction = step?.Name ?? action.StepId.ToString(),
                        DeviceName = deviceName,
                        DecisionReason = decision.Reason,
                        SimulatedOutcome = action.State.ToString(),
                        Error = action.State is ActionState.Failed or ActionState.TimedOut
                            ? action.Message
                            : null
                    });
                }

                triggerReason = $"{triggerReason} — sequenza terminata con stato {result.FinalState} " +
                                $"({result.Actions.Count} azioni).";
            }

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
