using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Contracts.Dto;

/// <summary>
/// Stato UPS per dashboard e API. Ogni metrica è nullable: la UI deve distinguere
/// "non disponibile" da un valore, e non mostrare mai valori inventati (§5.1).
/// </summary>
public sealed record UpsStatusDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Serial { get; init; }

    /// <summary>Stato NUT in forma testuale (es. "OL CHRG"). "UNKNOWN" se non determinabile.</summary>
    public required string StatusText { get; init; }

    public required bool IsOnLine { get; init; }
    public required bool IsOnBattery { get; init; }
    public required bool IsLowBattery { get; init; }

    public decimal? BatteryChargePercent { get; init; }
    public int? BatteryRuntimeSeconds { get; init; }
    public decimal? BatteryVoltage { get; init; }
    public decimal? InputVoltage { get; init; }
    public decimal? OutputVoltage { get; init; }
    public decimal? InputFrequency { get; init; }
    public decimal? OutputFrequency { get; init; }
    public decimal? LoadPercent { get; init; }
    public decimal? RealPowerWatts { get; init; }

    public IReadOnlyDictionary<string, string> PhaseData { get; init; } =
        new Dictionary<string, string>();

    public DateTimeOffset? LastCommunicationAt { get; init; }
    public required bool NutConnected { get; init; }
    public string? NutError { get; init; }

    /// <summary>Vero se il dato è obsoleto: va evidenziato nella UI (§5.3).</summary>
    public required bool IsStale { get; init; }
}

public sealed record InfrastructureStatusDto
{
    public required int TotalDevices { get; init; }
    public required int DevicesOn { get; init; }
    public required int DevicesOff { get; init; }
    public required int DevicesUnreachable { get; init; }
    public required int DevicesShuttingDown { get; init; }
    public required int DevicesStarting { get; init; }
    public required int DevicesWithErrors { get; init; }
    public required int DevicesUnknown { get; init; }
}

public sealed record AutomationStatusDto
{
    public required ExecutionMode ExecutionMode { get; init; }
    public required bool AutomationDisabled { get; init; }
    public IReadOnlyList<string> LiveEnabledAdapters { get; init; } = [];

    public required string OrchestratorState { get; init; }
    public DateTimeOffset? OrchestratorLastHeartbeat { get; init; }
    public required string SafetyAgentState { get; init; }
    public DateTimeOffset? SafetyAgentLastHeartbeat { get; init; }

    public string? AppliedRuleName { get; init; }
    public string? DecisionReason { get; init; }

    public string? CurrentBlackoutId { get; init; }
    public int? BlackoutDurationSeconds { get; init; }
    public bool BlackoutDurationCertain { get; init; } = true;
    public int? RemainingRuntimeSeconds { get; init; }
    public int? SecondsToNextThreshold { get; init; }

    public string? CurrentSequenceName { get; init; }
    public string? NextPlannedAction { get; init; }
    public long ActiveConfigVersion { get; init; }

    /// <summary>Vero se la decisione corrente si basa su dati incerti o incompleti.</summary>
    public bool BasedOnUncertainData { get; init; }
}
