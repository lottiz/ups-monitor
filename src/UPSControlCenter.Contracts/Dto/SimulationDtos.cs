using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Contracts.Dto;

/// <summary>Parametri dello scenario di simulazione (§14).</summary>
public sealed record SimulationScenarioDto
{
    public string Name { get; init; } = "Scenario senza nome";

    /// <summary>Durata del blackout simulato, in secondi.</summary>
    public int BlackoutDurationSeconds { get; init; } = 1800;

    /// <summary>Autonomia iniziale dichiarata. Null simula un UPS che non fornisce il dato.</summary>
    public int? InitialRuntimeSeconds { get; init; } = 2400;

    public decimal? InitialBatteryChargePercent { get; init; } = 100m;
    public decimal? LoadPercent { get; init; } = 30m;

    /// <summary>Secondi di autonomia consumati per ogni secondo di blackout.</summary>
    public decimal RuntimeDecayPerSecond { get; init; } = 1.5m;

    /// <summary>Istante in cui l'UPS segnala LB. Null = mai.</summary>
    public int? LowBatteryAtSecond { get; init; }

    /// <summary>Intervallo in cui la comunicazione NUT è persa.</summary>
    public int? CommunicationLostFromSecond { get; init; }
    public int? CommunicationLostToSecond { get; init; }

    /// <summary>Istante del ritorno della corrente. Null = non torna entro la simulazione.</summary>
    public int? PowerRestoredAtSecond { get; init; }

    /// <summary>Secondo blackout dopo il ripristino (§14).</summary>
    public int? SecondBlackoutAtSecond { get; init; }
    public int? SecondBlackoutDurationSeconds { get; init; }

    /// <summary>Dispositivi che simulano l'irraggiungibilità.</summary>
    public IReadOnlyList<Guid> UnreachableDeviceIds { get; init; } = [];

    /// <summary>Dispositivi che simulano un timeout di spegnimento.</summary>
    public IReadOnlyList<Guid> TimeoutDeviceIds { get; init; } = [];

    /// <summary>Istante di crash simulato dell'orchestrator: verifica il takeover del Safety Agent.</summary>
    public int? OrchestratorCrashAtSecond { get; init; }

    /// <summary>Istante di indisponibilità simulata del database.</summary>
    public int? DatabaseUnavailableAtSecond { get; init; }

    public int TickSeconds { get; init; } = 10;
}

public sealed record SimulationTimelineEntryDto
{
    public required int SimulatedSecond { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string UpsStatus { get; init; }
    public int? RemainingRuntimeSeconds { get; init; }
    public decimal? BatteryChargePercent { get; init; }
    public string? AppliedRule { get; init; }
    public string? PlannedAction { get; init; }
    public string? DeviceName { get; init; }
    public string? SimulatedOutcome { get; init; }
    public string? Error { get; init; }
    public required string DecisionReason { get; init; }
    public IReadOnlyList<UpsEventType> Events { get; init; } = [];
}

public sealed record SimulationResultDto
{
    public required Guid Id { get; init; }
    public required string ScenarioName { get; init; }
    public required bool ShutdownTriggered { get; init; }
    public int? ShutdownTriggeredAtSecond { get; init; }
    public string? TriggerReason { get; init; }
    public required int SimulatedDurationSeconds { get; init; }
    public required IReadOnlyList<SimulationTimelineEntryDto> Timeline { get; init; }

    /// <summary>
    /// Sempre vero: il simulatore non può raggiungere adapter reali, per costruzione (§14).
    /// Esposto per rendere la garanzia verificabile anche dall'esterno.
    /// </summary>
    public required bool AllActionsSimulated { get; init; }

    public required string Summary { get; init; }
}
