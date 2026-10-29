using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Tests.Rules;

/// <summary>
/// Scenari di blackout richiesti da §21. Ogni test riproduce un caso operativo reale e verifica
/// la decisione del motore, non la sua implementazione interna.
/// </summary>
public sealed class BlackoutScenarioTests
{
    private readonly RuleEvaluator _evaluator = new();

    /// <summary>
    /// Esegue un blackout simulato per la durata indicata e restituisce la prima decisione di
    /// spegnimento, con il secondo in cui è avvenuta.
    /// </summary>
    private (RuleDecision Decision, int? TriggeredAtSecond) RunBlackout(
        int durationSeconds,
        Core.Entities.ShutdownPolicy policy,
        Func<int, int?>? runtimeAt = null,
        Func<int, bool>? lowBatteryAt = null,
        int? powerRestoredAt = null,
        int tickSeconds = 10)
    {
        var clock = new TestClock();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId,
            policy.DebounceSamples);

        RuleDecision last = RuleDecision.NoAction("Nessuna valutazione eseguita.");

        for (var second = 0; second <= durationSeconds; second += tickSeconds)
        {
            var onBattery = powerRestoredAt is null || second < powerRestoredAt;
            var runtime = runtimeAt?.Invoke(second) ?? 3600;
            var lb = lowBatteryAt?.Invoke(second) ?? false;

            var snapshot = onBattery
                ? TestData.OnBattery(clock.UtcNow, runtime, 80m, lb)
                : TestData.OnLine(clock.UtcNow, runtime);

            var outcome = tracker.Process(snapshot);

            var ctx = new EvaluationContext
            {
                Snapshot = snapshot,
                Policy = policy,
                Blackout = outcome.Blackout,
                DebouncedStatus = outcome.DebouncedStatus,
                NowUtc = clock.UtcNow,
                NowMonotonicMs = clock.MonotonicMilliseconds,
                BootId = TestData.BootId,
                OrchestratorHeartbeatAge = TimeSpan.Zero
            };

            last = _evaluator.Evaluate(ctx, []);

            if (last.ShouldTriggerShutdown) return (last, second);

            clock.Advance(TimeSpan.FromSeconds(tickSeconds));
        }

        return (last, null);
    }

    [Fact]
    public void Blackout_di_5_minuti_non_avvia_la_sequenza()
    {
        var (decision, at) = RunBlackout(300, TestData.Policy30Plus30());

        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Null(at);
    }

    [Fact]
    public void Blackout_di_20_minuti_non_avvia_la_sequenza()
    {
        // Autonomia abbondante e durata sotto soglia: nessuna delle due condizioni è soddisfatta.
        var (decision, at) = RunBlackout(1200, TestData.Policy30Plus30(),
            runtimeAt: _ => 7200);

        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Null(at);
    }

    [Fact]
    public void Blackout_di_30_minuti_avvia_la_sequenza_per_durata()
    {
        var (decision, at) = RunBlackout(2400, TestData.Policy30Plus30(),
            runtimeAt: _ => 7200);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.NotNull(at);

        // La soglia è 1800s; con il debounce di 3 campioni il blackout inizia qualche secondo
        // dopo l'inizio della simulazione, quindi lo scatto cade subito dopo i 1800s.
        Assert.InRange(at!.Value, 1800, 1900);
        Assert.Contains("Blackout continuo", decision.Reason);
    }

    [Fact]
    public void Autonomia_sotto_30_minuti_avvia_la_sequenza_prima_della_soglia_di_durata()
    {
        // Autonomia che scende: la regola 30+30 scatta alla PRIMA delle due condizioni (§9.1).
        var (decision, at) = RunBlackout(3600, TestData.Policy30Plus30(),
            runtimeAt: second => Math.Max(0, 2100 - second));

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.NotNull(at);

        // 2100 - 300 = 1800 ⇒ attorno al secondo 300, molto prima dei 1800s di durata.
        Assert.InRange(at!.Value, 290, 320);
        Assert.Contains("Autonomia residua", decision.Reason);
    }

    [Fact]
    public void Stato_LB_avvia_immediatamente_la_sequenza_senza_attendere_il_debounce()
    {
        // LB è una condizione terminale dichiarata dall'UPS: attendere il debounce significherebbe
        // perdere secondi preziosi quando la batteria è già agli sgoccioli (§7.3).
        var (decision, at) = RunBlackout(3600, TestData.Policy30Plus30(),
            runtimeAt: _ => 7200,
            lowBatteryAt: second => second >= 120);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.NotNull(at);
        Assert.InRange(at!.Value, 120, 130);
        Assert.Contains("LB", decision.Reason);
    }

    [Fact]
    public void Ritorno_corrente_prima_della_soglia_annulla_il_timer()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId,
            policy.DebounceSamples);

        // 10 minuti su batteria, poi ritorno corrente.
        for (var second = 0; second < 600; second += 10)
        {
            tracker.Process(TestData.OnBattery(clock.UtcNow, 7200));
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.NotNull(tracker.Current);
        Assert.True(tracker.Current!.IsOpen);

        // Il ritorno richiede il debounce: tre letture OL consecutive.
        TrackerOutcome? outcome = null;
        for (var i = 0; i < policy.DebounceSamples; i++)
        {
            outcome = tracker.Process(TestData.OnLine(clock.UtcNow, 7200));
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.NotNull(outcome);
        Assert.True(outcome!.BlackoutJustEnded);
        Assert.False(tracker.Current!.IsOpen);
        Assert.Equal("PowerRestored", tracker.Current.EndReason);
        Assert.Contains(UpsEventType.PowerRestored, outcome.Events);

        // Dopo la chiusura, anche superando i 30 minuti di orologio, non si avvia nulla.
        clock.Advance(TimeSpan.FromMinutes(40));
        var afterOutcome = tracker.Process(TestData.OnLine(clock.UtcNow, 7200));

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = TestData.OnLine(clock.UtcNow, 7200),
            Policy = policy,
            Blackout = afterOutcome.Blackout,
            DebouncedStatus = afterOutcome.DebouncedStatus,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.False(decision.ShouldTriggerShutdown);
    }

    [Fact]
    public void Secondo_blackout_dopo_il_ripristino_riceve_un_identificativo_distinto()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        tracker.Process(TestData.OnBattery(clock.UtcNow));
        var firstId = tracker.Current!.BlackoutId;

        clock.Advance(TimeSpan.FromMinutes(5));
        tracker.Process(TestData.OnLine(clock.UtcNow));
        Assert.False(tracker.Current!.IsOpen);

        // Secondo blackout, a distanza di tempo: deve essere un evento separato, altrimenti le
        // azioni idempotenti del primo bloccherebbero quelle del secondo (§1.4).
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.Process(TestData.OnBattery(clock.UtcNow));

        Assert.True(tracker.Current!.IsOpen);
        Assert.NotEqual(firstId, tracker.Current.BlackoutId);
    }

    [Fact]
    public void Perdita_comunicazione_NUT_non_avvia_sequenze_con_la_policy_predefinita()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        var snapshot = UpsSnapshot.Unreachable(TestData.UpsName, clock.UtcNow,
            "Connessione rifiutata");
        var outcome = tracker.Process(snapshot);

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = policy,
            Blackout = outcome.Blackout,
            DebouncedStatus = outcome.DebouncedStatus,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Contains(UpsEventType.CommunicationLost, decision.CriticalConditions);
        Assert.True(decision.BasedOnUncertainData);
        Assert.Contains(UpsEventType.CommunicationLost, outcome.Events);
    }

    [Fact]
    public void Dati_obsoleti_sono_segnalati_e_non_interpretati_come_alimentazione_presente()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        var snapshot = TestData.Stale(clock.UtcNow);
        var outcome = tracker.Process(snapshot);

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = policy,
            Blackout = outcome.Blackout,
            DebouncedStatus = outcome.DebouncedStatus,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Contains(UpsEventType.StaleData, decision.CriticalConditions);
        Assert.True(decision.BasedOnUncertainData);
    }

    [Fact]
    public void Perdita_comunicazione_durante_blackout_avvia_la_sequenza_con_policy_esplicita()
    {
        // Policy alternativa: dopo la grazia, la perdita di comunicazione durante un blackout
        // viene trattata come blackout confermato.
        var policy = TestData.Policy30Plus30(p =>
        {
            p.CommunicationLostPolicy = CommunicationLostPolicy.TreatAsBlackoutAfterGrace;
            p.CommunicationLostGraceSeconds = 60;
        });

        var clock = new TestClock();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        tracker.Process(TestData.OnBattery(clock.UtcNow, 7200));
        clock.Advance(TimeSpan.FromSeconds(120));

        var snapshot = UpsSnapshot.Unreachable(TestData.UpsName, clock.UtcNow, "timeout");
        var outcome = tracker.Process(snapshot);

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = policy,
            Blackout = outcome.Blackout,
            DebouncedStatus = outcome.DebouncedStatus,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.True(decision.BasedOnUncertainData);
        Assert.Contains("TreatAsBlackoutAfterGrace", decision.Reason);
    }
}
