using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Core.Rules;

/// <summary>
/// Motore decisionale. <b>È l'unico</b> usato da Orchestrator, Safety Agent e Simulatore:
/// una simulazione e un blackout reale producono necessariamente la stessa decisione (§14).
/// </summary>
public sealed class RuleEvaluator
{
    /// <summary>Durata massima considerata plausibile per un blackout ricostruito (48 ore).</summary>
    public static readonly TimeSpan MaxPlausibleBlackoutDuration = TimeSpan.FromHours(48);

    public RuleDecision Evaluate(EvaluationContext ctx, IReadOnlyList<ShutdownRule> rules)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(rules);

        // Il kill switch ha precedenza assoluta su qualunque regola: §8.3.
        if (ctx.AutomationDisabled)
            return RuleDecision.NoAction("Automazione disabilitata dal kill switch: nessuna azione.",
                [UpsEventType.AutomationDisabled]);

        var critical = DetectCriticalConditions(ctx);

        // Dati non utilizzabili: si segnala e non si agisce, salvo policy esplicita.
        if (!ctx.Snapshot.CommunicationOk || ctx.Snapshot.IsStale)
        {
            if (ctx.Policy.CommunicationLostPolicy == CommunicationLostPolicy.TreatAsBlackoutAfterGrace
                && ctx.Blackout is { IsOpen: true } ob
                && ob.GetDuration(ctx.NowMonotonicMs, ctx.BootId, ctx.NowUtc)
                   >= TimeSpan.FromSeconds(ctx.Policy.CommunicationLostGraceSeconds))
            {
                return new RuleDecision
                {
                    ShouldTriggerShutdown = true,
                    Reason = "Comunicazione con NUT persa durante un blackout oltre la grazia " +
                             $"di {ctx.Policy.CommunicationLostGraceSeconds}s: policy TreatAsBlackoutAfterGrace.",
                    SequenceId = ctx.Policy.DefaultSequenceId,
                    CriticalConditions = critical,
                    BasedOnUncertainData = true
                };
            }

            return RuleDecision.NoAction(
                "Dati UPS non utilizzabili (comunicazione persa o obsoleti): monitoraggio attivo, nessuna azione.",
                critical, uncertain: true);
        }

        // Nessun blackout in corso: nulla da valutare per lo spegnimento.
        if (!ctx.DebouncedStatus.IsOnBattery())
            return RuleDecision.NoAction("UPS alimentato dalla rete: nessuna condizione di spegnimento.",
                critical);

        // Condizione terminale: LB è trattato separatamente dalle soglie configurabili (§9.3).
        if (ctx.DebouncedStatus.IsLowBattery())
        {
            return new RuleDecision
            {
                ShouldTriggerShutdown = true,
                Reason = "UPS in stato LB (batteria scarica): condizione terminale segnalata dall'UPS.",
                SequenceId = ctx.Policy.DefaultSequenceId,
                CriticalConditions = critical,
                SecondsToNextThreshold = 0
            };
        }

        // Regole personalizzate: valutate per priorità crescente prima delle soglie di base.
        foreach (var rule in rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority))
        {
            var tree = RuleSerialization.DeserializeCondition(rule.ConditionTreeJson);
            if (tree is null) continue;
            if (!IsWithinSchedule(rule, ctx.NowUtc)) continue;

            var result = ConditionEvaluator.Evaluate(tree, ctx);
            if (result == ConditionResult.True)
            {
                return new RuleDecision
                {
                    ShouldTriggerShutdown = true,
                    TriggeredRuleId = rule.Id,
                    TriggeredRuleName = rule.Name,
                    SequenceId = rule.TriggerSequenceId ?? ctx.Policy.DefaultSequenceId,
                    Reason = $"Regola «{rule.Name}» soddisfatta.",
                    CriticalConditions = critical,
                    BasedOnUncertainData = result == ConditionResult.Unknown
                };
            }
        }

        return EvaluateBaselineThresholds(ctx, critical);
    }

    /// <summary>
    /// Soglie di base "30 + 30": la sequenza parte alla <b>prima</b> fra durata del blackout e
    /// autonomia residua insufficiente (§9.1).
    /// </summary>
    private static RuleDecision EvaluateBaselineThresholds(EvaluationContext ctx,
        IReadOnlyList<UpsEventType> critical)
    {
        var maxDuration = TimeSpan.FromSeconds(ctx.Policy.MaxBlackoutDurationSeconds);
        var duration = ctx.BlackoutDuration;
        var uncertain = ctx.Blackout is { DurationCertain: false };

        // Durata incerta dopo un riavvio: si applica la policy conservativa documentata (§9.4).
        if (uncertain)
        {
            switch (ctx.Policy.UncertainDurationPolicy)
            {
                case UncertainDurationPolicy.TreatAsImmediateThreshold:
                    return new RuleDecision
                    {
                        ShouldTriggerShutdown = true,
                        Reason = "Durata del blackout non ricostruibile dopo il riavvio: policy " +
                                 "conservativa TreatAsImmediateThreshold, soglia considerata raggiunta.",
                        SequenceId = ctx.Policy.DefaultSequenceId,
                        CriticalConditions = [.. critical, UpsEventType.BlackoutDurationUncertain],
                        BasedOnUncertainData = true
                    };
                case UncertainDurationPolicy.RequireManualDecision:
                    return RuleDecision.NoAction(
                        "Durata del blackout incerta: policy RequireManualDecision, attesa di intervento manuale.",
                        [.. critical, UpsEventType.BlackoutDurationUncertain], uncertain: true);
                case UncertainDurationPolicy.RestartTimer:
                default:
                    break; // si procede con la durata disponibile
            }
        }

        if (duration is not null && duration >= maxDuration)
        {
            return new RuleDecision
            {
                ShouldTriggerShutdown = true,
                Reason = $"Blackout continuo da {(int)duration.Value.TotalSeconds}s " +
                         $"(soglia {ctx.Policy.MaxBlackoutDurationSeconds}s).",
                SequenceId = ctx.Policy.DefaultSequenceId,
                CriticalConditions = critical,
                SecondsToNextThreshold = 0,
                BasedOnUncertainData = uncertain
            };
        }

        var runtimeDecision = EvaluateRuntimeThreshold(ctx, critical, uncertain);
        if (runtimeDecision is not null) return runtimeDecision;

        // Nessuna soglia raggiunta: si calcola il tempo mancante alla più vicina, per la dashboard.
        var secondsToDuration = duration is null
            ? (int?)null
            : Math.Max(0, (int)(maxDuration - duration.Value).TotalSeconds);

        var secondsToRuntime = ctx.Snapshot.BatteryRuntimeSeconds is { } rt
            ? Math.Max(0, rt - ctx.Policy.MinRemainingRuntimeSeconds)
            : (int?)null;

        var next = (secondsToDuration, secondsToRuntime) switch
        {
            (null, null) => (int?)null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => Math.Min(a, b)
        };

        var durationText = duration is null ? "sconosciuta" : $"{(int)duration.Value.TotalSeconds}s";
        var runtimeText = ctx.Snapshot.BatteryRuntimeSeconds is { } r ? $"{r}s" : "non disponibile";

        return RuleDecision.NoAction(
            $"Blackout in corso: durata {durationText}, autonomia residua {runtimeText}. Nessuna soglia raggiunta.",
            critical, next, uncertain);
    }

    /// <summary>
    /// Valuta la soglia di autonomia residua gestendo esplicitamente il caso "valore sconosciuto",
    /// che non viene mai trattato come zero (§9.3, R5).
    /// </summary>
    private static RuleDecision? EvaluateRuntimeThreshold(EvaluationContext ctx,
        IReadOnlyList<UpsEventType> critical, bool uncertain)
    {
        var threshold = ctx.Policy.MinRemainingRuntimeSeconds;
        var runtime = ctx.Snapshot.BatteryRuntimeSeconds;

        if (runtime is { } seconds)
        {
            return seconds <= threshold
                ? new RuleDecision
                {
                    ShouldTriggerShutdown = true,
                    Reason = $"Autonomia residua stimata {seconds}s <= soglia {threshold}s durante lo stato OB.",
                    SequenceId = ctx.Policy.DefaultSequenceId,
                    CriticalConditions = critical,
                    SecondsToNextThreshold = 0,
                    BasedOnUncertainData = uncertain
                }
                : null;
        }

        // Autonomia sconosciuta: il comportamento è deciso da una policy esplicita, non da un default implicito.
        switch (ctx.Policy.UnknownRuntimePolicy)
        {
            case UnknownRuntimePolicy.TreatAsInsufficient:
                return new RuleDecision
                {
                    ShouldTriggerShutdown = true,
                    Reason = "Autonomia residua non disponibile durante un blackout: policy " +
                             "TreatAsInsufficient (il valore assente non è interpretato come zero né come illimitato).",
                    SequenceId = ctx.Policy.DefaultSequenceId,
                    CriticalConditions = [.. critical, UpsEventType.RuntimeUnknown],
                    BasedOnUncertainData = true
                };

            case UnknownRuntimePolicy.FallbackToBatteryCharge:
                if (ctx.Snapshot.BatteryChargePercent is { } charge
                    && ctx.Policy.CriticalBatteryChargePercent is { } min
                    && charge <= min)
                {
                    return new RuleDecision
                    {
                        ShouldTriggerShutdown = true,
                        Reason = $"Autonomia non disponibile; carica batteria {charge}% <= soglia {min}% " +
                                 "(policy FallbackToBatteryCharge).",
                        SequenceId = ctx.Policy.DefaultSequenceId,
                        CriticalConditions = [.. critical, UpsEventType.RuntimeUnknown],
                        BasedOnUncertainData = true
                    };
                }
                return null;

            case UnknownRuntimePolicy.IgnoreRuntimeCondition:
            default:
                return null;
        }
    }

    /// <summary>
    /// Rileva le condizioni critiche. Sono registrate e notificate a prescindere dalla decisione
    /// di spegnimento, perché richiedono attenzione anche quando non fanno scattare una sequenza.
    /// </summary>
    public static IReadOnlyList<UpsEventType> DetectCriticalConditions(EvaluationContext ctx)
    {
        var list = new List<UpsEventType>();

        if (!ctx.Snapshot.CommunicationOk) list.Add(UpsEventType.CommunicationLost);
        else if (ctx.Snapshot.IsStale) list.Add(UpsEventType.StaleData);

        if (ctx.DebouncedStatus.IsLowBattery()) list.Add(UpsEventType.LowBattery);

        if (ctx.Snapshot.BatteryChargePercent is { } charge
            && ctx.Policy.CriticalBatteryChargePercent is { } min
            && charge <= min)
            list.Add(UpsEventType.BatteryCritical);

        // Autonomia sconosciuta mentre si è su batteria: incertezza rilevante, va segnalata.
        if (ctx.DebouncedStatus.IsOnBattery() && ctx.Snapshot.BatteryRuntimeSeconds is null)
            list.Add(UpsEventType.RuntimeUnknown);

        if (ctx.Blackout is { DurationCertain: false })
            list.Add(UpsEventType.BlackoutDurationUncertain);

        // Carico anomalo: sopra il 100% o sovraccarico dichiarato dall'UPS.
        if (ctx.Snapshot.LoadPercent is > 100m || ctx.DebouncedStatus.HasFlag(UpsStatusFlags.Overload))
            list.Add(UpsEventType.LoadAnomaly);

        if (ctx.OrchestratorHeartbeatAge is { } age
            && age > TimeSpan.FromSeconds(ctx.Policy.TakeoverGraceSeconds))
            list.Add(UpsEventType.OrchestratorUnavailable);

        if (ctx.AutomationDisabled) list.Add(UpsEventType.AutomationDisabled);

        return list;
    }

    /// <summary>Verifica fasce orarie e giorni della settimana configurati sulla regola (§9.2).</summary>
    private static bool IsWithinSchedule(ShutdownRule rule, DateTimeOffset nowUtc)
    {
        if (rule.DaysOfWeekMask is { } mask && mask != 0)
        {
            var bit = 1 << (int)nowUtc.DayOfWeek;
            if ((mask & bit) == 0) return false;
        }

        if (string.IsNullOrWhiteSpace(rule.TimeWindowsJson)) return true;

        var windows = RuleSerialization.DeserializeTimeWindows(rule.TimeWindowsJson);
        if (windows.Count == 0) return true;

        var now = TimeOnly.FromDateTime(nowUtc.UtcDateTime);
        return windows.Any(w => w.Contains(now));
    }
}
