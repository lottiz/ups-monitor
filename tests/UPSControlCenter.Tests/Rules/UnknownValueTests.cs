using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Tests.Rules;

/// <summary>
/// Verifica il requisito §9.3: un valore mancante non viene mai interpretato come zero.
/// È il rischio R5, fra i più gravi del sistema, perché "autonomia 0" e "autonomia sconosciuta"
/// portano a decisioni opposte.
/// </summary>
public sealed class UnknownValueTests
{
    private readonly RuleEvaluator _evaluator = new();

    private RuleDecision Evaluate(Core.Entities.ShutdownPolicy policy, UpsSnapshot snapshot,
        BlackoutState? blackout, TestClock clock) =>
        _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = snapshot.Status,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

    private static (BlackoutState Blackout, TestClock Clock) OpenBlackout(TimeSpan elapsed)
    {
        var clock = new TestClock();
        var blackout = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        };

        clock.Advance(elapsed);
        return (blackout, clock);
    }

    [Fact]
    public void Autonomia_sconosciuta_con_TreatAsInsufficient_avvia_la_sequenza()
    {
        var policy = TestData.Policy30Plus30();
        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        // runtime = null simula un UPS che non espone battery.runtime.
        var snapshot = TestData.OnBattery(clock.UtcNow, runtime: null, charge: 90m);
        var decision = Evaluate(policy, snapshot, blackout, clock);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.True(decision.BasedOnUncertainData);
        Assert.Contains(UpsEventType.RuntimeUnknown, decision.CriticalConditions);
        Assert.Contains("non è interpretato come zero", decision.Reason);
    }

    [Fact]
    public void Autonomia_sconosciuta_con_IgnoreRuntimeCondition_lascia_agire_la_sola_durata()
    {
        var policy = TestData.Policy30Plus30(p =>
            p.UnknownRuntimePolicy = UnknownRuntimePolicy.IgnoreRuntimeCondition);

        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));
        var snapshot = TestData.OnBattery(clock.UtcNow, runtime: null, charge: 90m);

        var decision = Evaluate(policy, snapshot, blackout, clock);

        // Durata ancora sotto soglia: nessuna azione, ma l'incertezza è segnalata.
        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Contains(UpsEventType.RuntimeUnknown, decision.CriticalConditions);
    }

    [Fact]
    public void Autonomia_sconosciuta_con_IgnoreRuntimeCondition_scatta_sulla_durata()
    {
        var policy = TestData.Policy30Plus30(p =>
            p.UnknownRuntimePolicy = UnknownRuntimePolicy.IgnoreRuntimeCondition);

        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(31));
        var snapshot = TestData.OnBattery(clock.UtcNow, runtime: null, charge: 90m);

        var decision = Evaluate(policy, snapshot, blackout, clock);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.Contains("Blackout continuo", decision.Reason);
    }

    [Fact]
    public void Autonomia_sconosciuta_con_FallbackToBatteryCharge_usa_la_carica()
    {
        var policy = TestData.Policy30Plus30(p =>
        {
            p.UnknownRuntimePolicy = UnknownRuntimePolicy.FallbackToBatteryCharge;
            p.CriticalBatteryChargePercent = 25m;
        });

        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        // Carica sopra soglia: nessuna azione.
        var healthy = TestData.OnBattery(clock.UtcNow, runtime: null, charge: 60m);
        Assert.False(Evaluate(policy, healthy, blackout, clock).ShouldTriggerShutdown);

        // Carica sotto soglia: si agisce usando l'indicatore sostitutivo.
        var low = TestData.OnBattery(clock.UtcNow, runtime: null, charge: 20m);
        var decision = Evaluate(policy, low, blackout, clock);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.Contains("FallbackToBatteryCharge", decision.Reason);
    }

    [Fact]
    public void Autonomia_zero_e_autonomia_sconosciuta_producono_motivazioni_diverse()
    {
        var policy = TestData.Policy30Plus30();
        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        var zero = Evaluate(policy, TestData.OnBattery(clock.UtcNow, runtime: 0), blackout, clock);
        var unknown = Evaluate(policy, TestData.OnBattery(clock.UtcNow, runtime: null), blackout, clock);

        // Entrambi portano allo spegnimento, ma per ragioni diverse: la distinzione deve restare
        // visibile nell'audit e nella dashboard.
        Assert.True(zero.ShouldTriggerShutdown);
        Assert.True(unknown.ShouldTriggerShutdown);
        Assert.NotEqual(zero.Reason, unknown.Reason);
        Assert.False(zero.BasedOnUncertainData);
        Assert.True(unknown.BasedOnUncertainData);
    }

    [Fact]
    public void Condizione_su_valore_mancante_restituisce_Unknown_non_False()
    {
        var policy = TestData.Policy30Plus30();
        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        var ctx = new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, runtime: null),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        };

        var condition = ConditionNode.Numeric(ConditionKind.RemainingRuntimeSeconds,
            ConditionOperator.LessThanOrEqual, 1800);

        // Collassare l'ignoto su "false" nasconderebbe l'incertezza al posto di segnalarla.
        Assert.Equal(ConditionResult.Unknown, ConditionEvaluator.Evaluate(condition, ctx));
    }

    [Fact]
    public void AND_di_Kleene_resta_falso_se_un_ramo_e_falso_anche_con_ignoti()
    {
        var policy = TestData.Policy30Plus30();
        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        var ctx = new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, runtime: null),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        };

        var tree = ConditionNode.And(
            ConditionNode.Flag("OL"),                                     // falso: siamo su OB
            ConditionNode.Numeric(ConditionKind.RemainingRuntimeSeconds,  // ignoto
                ConditionOperator.LessThanOrEqual, 1800));

        Assert.Equal(ConditionResult.False, ConditionEvaluator.Evaluate(tree, ctx));
    }

    [Fact]
    public void OR_di_Kleene_resta_vero_se_un_ramo_e_vero_anche_con_ignoti()
    {
        var policy = TestData.Policy30Plus30();
        var (blackout, clock) = OpenBlackout(TimeSpan.FromMinutes(1));

        var ctx = new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, runtime: null),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        };

        var tree = ConditionNode.Or(
            ConditionNode.Flag("OB"),                                     // vero
            ConditionNode.Numeric(ConditionKind.RemainingRuntimeSeconds,  // ignoto
                ConditionOperator.LessThanOrEqual, 1800));

        Assert.Equal(ConditionResult.True, ConditionEvaluator.Evaluate(tree, ctx));
    }
}
