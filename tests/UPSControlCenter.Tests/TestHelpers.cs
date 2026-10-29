using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Tests;

/// <summary>Orologio controllabile: i test non devono dipendere dal tempo reale.</summary>
public sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private long _monotonic;

    public TestClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow => _now;
    public long MonotonicMilliseconds => _monotonic;

    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
        _monotonic += (long)delta.TotalMilliseconds;
    }

    /// <summary>Sposta solo l'orologio di parete: riproduce una correzione NTP (§9.4).</summary>
    public void SkewWallClockOnly(TimeSpan delta) => _now = _now.Add(delta);
}

public static class TestData
{
    public const string BootId = "test-boot-1";
    public const string UpsName = "riello";

    /// <summary>Policy "30 + 30" con i valori predefiniti del progetto (§9.1).</summary>
    public static ShutdownPolicy Policy30Plus30(Action<ShutdownPolicy>? customize = null)
    {
        var policy = new ShutdownPolicy
        {
            Name = "Policy 30+30",
            IsActive = true,
            ExecutionMode = ExecutionMode.DryRun,
            MaxBlackoutDurationSeconds = 1800,
            MinRemainingRuntimeSeconds = 1800,
            DebounceSamples = 3,
            CriticalBatteryChargePercent = 20m,
            UnknownRuntimePolicy = UnknownRuntimePolicy.TreatAsInsufficient,
            UncertainDurationPolicy = UncertainDurationPolicy.TreatAsImmediateThreshold,
            CommunicationLostPolicy = CommunicationLostPolicy.HoldAndAlert,
            CommunicationLostGraceSeconds = 120,
            TakeoverGraceSeconds = 90
        };

        customize?.Invoke(policy);
        return policy;
    }

    public static UpsSnapshot OnLine(DateTimeOffset at, int? runtime = 3600, decimal? charge = 100m) =>
        new()
        {
            UpsName = UpsName,
            AcquiredAt = at,
            StatusRaw = "OL CHRG",
            Status = UpsStatusFlags.OnLine | UpsStatusFlags.Charging,
            CommunicationOk = true,
            IsStale = false,
            BatteryRuntimeSeconds = runtime,
            BatteryChargePercent = charge,
            LoadPercent = 30m,
            InputVoltage = 230m,
            OutputVoltage = 230m
        };

    public static UpsSnapshot OnBattery(DateTimeOffset at, int? runtime = 3600,
        decimal? charge = 100m, bool lowBattery = false) =>
        new()
        {
            UpsName = UpsName,
            AcquiredAt = at,
            StatusRaw = lowBattery ? "OB LB DISCHRG" : "OB DISCHRG",
            Status = UpsStatusFlags.OnBattery | UpsStatusFlags.Discharging
                     | (lowBattery ? UpsStatusFlags.LowBattery : UpsStatusFlags.None),
            CommunicationOk = true,
            IsStale = false,
            BatteryRuntimeSeconds = runtime,
            BatteryChargePercent = charge,
            LoadPercent = 30m,
            InputVoltage = 0m,
            OutputVoltage = 230m
        };

    public static UpsSnapshot Stale(DateTimeOffset at) =>
        OnBattery(at) with { IsStale = true };

    public static ManagedDevice Device(string name, DeviceType type = DeviceType.LinuxServer,
        int priority = 500) => new()
        {
            Name = name,
            Hostname = $"{name.ToLowerInvariant()}.local",
            DeviceType = type,
            Priority = priority,
            ShutdownProtocol = ShutdownProtocol.Ssh,
            StartupProtocol = StartupProtocol.WakeOnLan,
            ShutdownTimeoutSeconds = 300,
            StartupTimeoutSeconds = 600,
            PostActionDelaySeconds = 10,
            StatusCheckMethod = StatusCheckMethod.TcpPort,
            IsEnabled = true
        };
}
