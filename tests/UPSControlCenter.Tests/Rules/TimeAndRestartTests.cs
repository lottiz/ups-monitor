using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Tests.Rules;

/// <summary>
/// Sicurezza temporale (§9.4): tempo monotono, riavvii, correzioni d'orologio e debounce.
/// </summary>
public sealed class TimeAndRestartTests
{
    private readonly RuleEvaluator _evaluator = new();

    [Fact]
    public void La_durata_usa_il_tempo_monotono_e_ignora_le_correzioni_di_orologio()
    {
        var clock = new TestClock();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        tracker.Process(TestData.OnBattery(clock.UtcNow));
        var blackout = tracker.Current!;

        clock.Advance(TimeSpan.FromMinutes(10));

        // NTP sposta l'orologio avanti di un'ora: la durata reale resta 10 minuti.
        clock.SkewWallClockOnly(TimeSpan.FromHours(1));

        var duration = blackout.GetDuration(clock.MonotonicMilliseconds, TestData.BootId,
            clock.UtcNow);

        Assert.Equal(10, (int)duration.TotalMinutes);
    }

    [Fact]
    public void Correzione_di_orologio_non_fa_scattare_la_soglia_in_anticipo()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId, 1);

        tracker.Process(TestData.OnBattery(clock.UtcNow, 7200));
        var blackout = tracker.Current!;

        clock.Advance(TimeSpan.FromMinutes(5));
        clock.SkewWallClockOnly(TimeSpan.FromHours(2)); // salto d'orologio

        var snapshot = TestData.OnBattery(clock.UtcNow, 7200);
        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        // Solo 5 minuti effettivi: non si deve spegnere l'infrastruttura per un salto di NTP.
        Assert.False(decision.ShouldTriggerShutdown);
    }

    [Fact]
    public void Riavvio_con_boot_diverso_e_durata_plausibile_mantiene_la_certezza()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var restored = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = start,
            StartedAtMonotonicMs = 50_000,
            BootId = "boot-precedente"
        };

        // 20 minuti dopo, nuovo boot: ricostruzione plausibile dall'orologio di parete.
        var now = start.AddMinutes(20);
        var result = BlackoutTracker.Reconcile(restored, "boot-nuovo", now, TimeSpan.FromHours(48));

        Assert.True(result.DurationCertain);
        Assert.Equal("boot-nuovo", result.BootId);
        Assert.Equal(0, result.StartedAtMonotonicMs);
    }

    [Fact]
    public void Riavvio_con_durata_implausibile_marca_la_durata_come_incerta()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var restored = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = start,
            StartedAtMonotonicMs = 50_000,
            BootId = "boot-precedente"
        };

        // 5 giorni dopo: oltre la durata massima plausibile di un blackout.
        var now = start.AddDays(5);
        var result = BlackoutTracker.Reconcile(restored, "boot-nuovo", now, TimeSpan.FromHours(48));

        Assert.False(result.DurationCertain);
    }

    [Fact]
    public void Orologio_spostato_indietro_rende_la_durata_incerta()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var restored = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = start,
            StartedAtMonotonicMs = 50_000,
            BootId = "boot-precedente"
        };

        // Durata negativa: l'orologio è stato spostato indietro, il dato non è utilizzabile.
        var result = BlackoutTracker.Reconcile(restored, "boot-nuovo", start.AddMinutes(-30),
            TimeSpan.FromHours(48));

        Assert.False(result.DurationCertain);
    }

    [Fact]
    public void Durata_incerta_applica_la_policy_conservativa_predefinita()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30(); // TreatAsImmediateThreshold

        var blackout = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMs = 0,
            BootId = TestData.BootId,
            DurationCertain = false
        };

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, 7200),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.True(decision.ShouldTriggerShutdown);
        Assert.True(decision.BasedOnUncertainData);
        Assert.Contains(UpsEventType.BlackoutDurationUncertain, decision.CriticalConditions);
        Assert.Contains("TreatAsImmediateThreshold", decision.Reason);
    }

    [Fact]
    public void Durata_incerta_con_RequireManualDecision_non_agisce()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30(p =>
            p.UncertainDurationPolicy = UncertainDurationPolicy.RequireManualDecision);

        var blackout = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMs = 0,
            BootId = TestData.BootId,
            DurationCertain = false
        };

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, 7200),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        }, []);

        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Contains("RequireManualDecision", decision.Reason);
    }

    [Fact]
    public void Il_debounce_scarta_le_micro_interruzioni()
    {
        var clock = new TestClock();
        var tracker = new BlackoutTracker(TestData.UpsName, clock, TestData.BootId,
            debounceSamples: 3);

        // Stato iniziale stabile OL.
        for (var i = 0; i < 3; i++)
        {
            tracker.Process(TestData.OnLine(clock.UtcNow));
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        // Due sole letture OB: sotto il debounce, nessun blackout aperto.
        tracker.Process(TestData.OnBattery(clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(10));
        tracker.Process(TestData.OnBattery(clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(tracker.Current);

        // Terza lettura concorde: il blackout viene confermato.
        tracker.Process(TestData.OnBattery(clock.UtcNow));

        Assert.NotNull(tracker.Current);
        Assert.True(tracker.Current!.IsOpen);
    }

    [Fact]
    public void Il_blackout_id_e_deterministico_fra_osservatori_distinti()
    {
        // Due processi che rilevano lo stesso evento a qualche secondo di distanza devono
        // calcolare lo stesso identificativo, senza coordinarsi (§1.4).
        var instant = new DateTimeOffset(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

        var orchestratorId = BlackoutState.ComputeId(TestData.UpsName, instant);
        var agentId = BlackoutState.ComputeId(TestData.UpsName, instant.AddSeconds(8));

        Assert.Equal(orchestratorId, agentId);
    }

    [Fact]
    public void Blackout_distanti_nel_tempo_hanno_identificativi_differenti()
    {
        var instant = new DateTimeOffset(2026, 3, 15, 14, 30, 5, TimeSpan.Zero);

        var first = BlackoutState.ComputeId(TestData.UpsName, instant);
        var second = BlackoutState.ComputeId(TestData.UpsName, instant.AddMinutes(10));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Il_kill_switch_impedisce_qualunque_azione()
    {
        var clock = new TestClock();
        var policy = TestData.Policy30Plus30();

        var blackout = new BlackoutState
        {
            BlackoutId = "riello:1",
            UpsName = TestData.UpsName,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId
        };

        clock.Advance(TimeSpan.FromHours(2)); // soglia largamente superata

        var decision = _evaluator.Evaluate(new EvaluationContext
        {
            Snapshot = TestData.OnBattery(clock.UtcNow, runtime: 10, lowBattery: true),
            Policy = policy,
            Blackout = blackout,
            DebouncedStatus = UpsStatusFlags.OnBattery | UpsStatusFlags.LowBattery,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = TestData.BootId,
            AutomationDisabled = true
        }, []);

        // Anche con LB e soglia superata: il kill switch ha precedenza assoluta (§8.3).
        Assert.False(decision.ShouldTriggerShutdown);
        Assert.Contains(UpsEventType.AutomationDisabled, decision.CriticalConditions);
    }
}
