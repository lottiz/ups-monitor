using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Genera letture NUT secondo lo scenario. Non apre alcuna connessione: il simulatore non può
/// raggiungere né NUT né i dispositivi (§14).
/// </summary>
public sealed class SimulatedNutClient(SimulationScenarioDto scenario, string upsName) : INutClient
{
    private int _currentSecond;

    /// <summary>Secondo corrente della simulazione, impostato dal motore prima di ogni lettura.</summary>
    public int CurrentSecond
    {
        get => _currentSecond;
        set => _currentSecond = value;
    }

    public Task<IReadOnlyList<string>> ListUpsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([upsName]);

    public Task<UpsSnapshot> ReadAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Build(_currentSecond, DateTimeOffset.UnixEpoch.AddSeconds(_currentSecond)));

    /// <summary>Costruisce la lettura corrispondente al secondo indicato dello scenario.</summary>
    public UpsSnapshot Build(int second, DateTimeOffset at)
    {
        // Comunicazione persa: lo snapshot non riporta metriche, come nella realtà.
        if (IsCommunicationLost(second))
            return UpsSnapshot.Unreachable(upsName, at, "Comunicazione NUT persa (simulata).");

        var onBattery = IsOnBattery(second);
        var flags = onBattery ? UpsStatusFlags.OnBattery | UpsStatusFlags.Discharging
                              : UpsStatusFlags.OnLine | UpsStatusFlags.Charging;

        if (scenario.LowBatteryAtSecond is { } lb && second >= lb && onBattery)
            flags |= UpsStatusFlags.LowBattery;

        return new UpsSnapshot
        {
            UpsName = upsName,
            AcquiredAt = at,
            StatusRaw = flags.ToNutString(),
            Status = flags,
            CommunicationOk = true,
            IsStale = false,
            BatteryRuntimeSeconds = ComputeRuntime(second, onBattery),
            BatteryChargePercent = ComputeCharge(second, onBattery),
            LoadPercent = scenario.LoadPercent,
            InputVoltage = onBattery ? 0m : 230m,
            OutputVoltage = 230m,
            InputFrequency = onBattery ? 0m : 50m,
            OutputFrequency = 50m
        };
    }

    private bool IsCommunicationLost(int second)
    {
        if (scenario.CommunicationLostFromSecond is not { } from) return false;
        var to = scenario.CommunicationLostToSecond ?? int.MaxValue;
        return second >= from && second <= to;
    }

    /// <summary>
    /// Determina se l'UPS è su batteria, tenendo conto del ritorno della corrente e di un
    /// eventuale secondo blackout (§14).
    /// </summary>
    private bool IsOnBattery(int second)
    {
        if (scenario.SecondBlackoutAtSecond is { } sb && second >= sb)
        {
            var end = sb + (scenario.SecondBlackoutDurationSeconds ?? int.MaxValue - sb);
            if (second < end) return true;
            return false;
        }

        if (scenario.PowerRestoredAtSecond is { } restored && second >= restored)
            return false;

        return second < scenario.BlackoutDurationSeconds;
    }

    /// <summary>
    /// Autonomia residua. Se lo scenario dichiara <c>null</c>, resta null per tutta la
    /// simulazione: riproduce un UPS che non fornisce <c>battery.runtime</c> (§9.3).
    /// </summary>
    private int? ComputeRuntime(int second, bool onBattery)
    {
        if (scenario.InitialRuntimeSeconds is not { } initial) return null;
        if (!onBattery) return initial;

        var elapsed = ElapsedOnBattery(second);
        var remaining = initial - (int)(elapsed * scenario.RuntimeDecayPerSecond);
        return Math.Max(0, remaining);
    }

    private decimal? ComputeCharge(int second, bool onBattery)
    {
        if (scenario.InitialBatteryChargePercent is not { } initial) return null;
        if (!onBattery) return initial;

        // Scarica proporzionale all'autonomia iniziale dichiarata, limitata a [0,100].
        var elapsed = ElapsedOnBattery(second);
        var total = scenario.InitialRuntimeSeconds ?? 2400;
        var consumed = total <= 0 ? 0m : (decimal)elapsed / total * initial;
        return Math.Clamp(initial - consumed, 0m, 100m);
    }

    private int ElapsedOnBattery(int second)
    {
        if (scenario.SecondBlackoutAtSecond is { } sb && second >= sb)
            return second - sb;
        return second;
    }
}
