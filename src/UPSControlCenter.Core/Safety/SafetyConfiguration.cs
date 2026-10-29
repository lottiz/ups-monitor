using System.Text.Json.Serialization;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Safety;

/// <summary>
/// Configurazione autonoma del Safety Agent: tutto ciò che serve a decidere e agire <b>senza</b>
/// accesso a PostgreSQL. Viene versionata, firmata e distribuita come unità atomica (§3.3).
/// </summary>
public sealed record SafetyConfiguration
{
    public required long Version { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Modalità di esecuzione. Il valore iniziale distribuito è <see cref="ExecutionMode.DryRun"/>.</summary>
    public required ExecutionMode ExecutionMode { get; init; }

    /// <summary>Adapter abilitati a operazioni reali. Vuoto in DRY_RUN (§8.2).</summary>
    public IReadOnlyList<string> LiveEnabledAdapters { get; init; } = [];

    public required SafetyUpsConfig Ups { get; init; }
    public required SafetyThresholds Thresholds { get; init; }
    public IReadOnlyList<SafetyDevice> Devices { get; init; } = [];
    public IReadOnlyList<SafetyStep> ShutdownSteps { get; init; } = [];
    public IReadOnlyList<SafetyDependency> Dependencies { get; init; } = [];

    /// <summary>Secondi senza heartbeat dell'orchestrator prima del takeover (§1.4).</summary>
    public int TakeoverGraceSeconds { get; init; } = 90;

    [JsonIgnore]
    public bool IsDryRun => ExecutionMode == ExecutionMode.DryRun;
}

public sealed record SafetyUpsConfig
{
    public required string NutHost { get; init; }
    public required int NutPort { get; init; }
    public required string NutUpsName { get; init; }
    public int PollingIntervalSeconds { get; init; } = 10;
    public int CommunicationTimeoutSeconds { get; init; } = 30;
    public int StaleDataThresholdSeconds { get; init; } = 60;
}

public sealed record SafetyThresholds
{
    public int MaxBlackoutDurationSeconds { get; init; } = 1800;
    public int MinRemainingRuntimeSeconds { get; init; } = 1800;
    public int DebounceSamples { get; init; } = 3;
    public decimal? CriticalBatteryChargePercent { get; init; } = 20m;
    public UnknownRuntimePolicy UnknownRuntimePolicy { get; init; } = UnknownRuntimePolicy.TreatAsInsufficient;
    public UncertainDurationPolicy UncertainDurationPolicy { get; init; } = UncertainDurationPolicy.TreatAsImmediateThreshold;
    public CommunicationLostPolicy CommunicationLostPolicy { get; init; } = CommunicationLostPolicy.HoldAndAlert;
    public int CommunicationLostGraceSeconds { get; init; } = 120;
}

public sealed record SafetyDevice
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Hostname { get; init; }
    public DeviceType DeviceType { get; init; }
    public ShutdownProtocol ShutdownProtocol { get; init; }
    public int Priority { get; init; } = 500;
    public int ShutdownTimeoutSeconds { get; init; } = 300;
    public int PostActionDelaySeconds { get; init; } = 10;
    public StatusCheckMethod StatusCheckMethod { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public sealed record SafetyStep
{
    public required Guid Id { get; init; }
    public required int OrderIndex { get; init; }
    public required string Name { get; init; }
    public required SequenceStepType StepType { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public IReadOnlyList<Guid> TargetDeviceIds { get; init; } = [];
    public int? ParallelGroup { get; init; }
    public int DelayBeforeSeconds { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxRetries { get; init; }
    public bool ContinueOnError { get; init; }
    public bool VerifyCompletion { get; init; } = true;
}

public sealed record SafetyDependency
{
    public required Guid DependentDeviceId { get; init; }
    public required Guid DependsOnDeviceId { get; init; }
    public DependencyType DependencyType { get; init; }
    public bool IsHard { get; init; } = true;
}
