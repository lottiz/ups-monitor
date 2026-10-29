using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

/// <summary>
/// Contenitore della configurazione di spegnimento. Una sola policy può essere attiva, garantito
/// da un indice univoco parziale nel database (§4.3).
/// </summary>
public class ShutdownPolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Modalità della policy. Il valore iniziale obbligatorio è <see cref="ExecutionMode.DryRun"/>.</summary>
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.DryRun;

    /// <summary>Regola "30 + 30": durata massima del blackout tollerata. Default 30 minuti.</summary>
    public int MaxBlackoutDurationSeconds { get; set; } = 1800;

    /// <summary>Regola "30 + 30": autonomia residua minima. Default 30 minuti.</summary>
    public int MinRemainingRuntimeSeconds { get; set; } = 1800;

    /// <summary>Letture concordi necessarie per accettare una transizione di stato (§9.4).</summary>
    public int DebounceSamples { get; set; } = 3;

    public UnknownRuntimePolicy UnknownRuntimePolicy { get; set; } = UnknownRuntimePolicy.TreatAsInsufficient;
    public UncertainDurationPolicy UncertainDurationPolicy { get; set; } = UncertainDurationPolicy.TreatAsImmediateThreshold;
    public PowerRestoredDuringShutdownPolicy PowerRestoredDuringShutdownPolicy { get; set; } = PowerRestoredDuringShutdownPolicy.CompleteThenRestart;
    public CommunicationLostPolicy CommunicationLostPolicy { get; set; } = CommunicationLostPolicy.HoldAndAlert;

    /// <summary>Grazia prima di considerare critica la perdita di comunicazione.</summary>
    public int CommunicationLostGraceSeconds { get; set; } = 120;

    /// <summary>Soglia di carica sotto la quale la batteria è considerata critica, se il dato esiste.</summary>
    public decimal? CriticalBatteryChargePercent { get; set; } = 20m;

    /// <summary>Secondi senza heartbeat dell'orchestrator prima che il Safety Agent esegua il takeover.</summary>
    public int TakeoverGraceSeconds { get; set; } = 90;

    public Guid? DefaultSequenceId { get; set; }
    public ShutdownSequence? DefaultSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ShutdownRule> Rules { get; set; } = [];
}

public class ShutdownRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShutdownPolicyId { get; set; }
    public ShutdownPolicy? ShutdownPolicy { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Albero di condizioni AND/OR serializzato in JSON (<c>Rules.ConditionNode</c>).</summary>
    public string ConditionTreeJson { get; set; } = "{}";

    public int Priority { get; set; } = 500;
    public bool IsEnabled { get; set; } = true;

    public Guid? TriggerSequenceId { get; set; }
    public ShutdownSequence? TriggerSequence { get; set; }

    /// <summary>Fasce orarie di validità in JSON. Null = sempre valida.</summary>
    public string? TimeWindowsJson { get; set; }

    /// <summary>Bitmask dei giorni della settimana (bit 0 = domenica). Null = tutti i giorni.</summary>
    public int? DaysOfWeekMask { get; set; }
}

public class StartupPolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.DryRun;

    /// <summary>Alimentazione stabile richiesta prima di riaccendere. Default 5 minuti (§12).</summary>
    public int RequiredStablePowerSeconds { get; set; } = 300;

    /// <summary>
    /// Letture consecutive OL necessarie: la stabilità non è valutata su una sola lettura (§12).
    /// </summary>
    public int StabilitySamples { get; set; } = 10;

    public decimal MinBatteryChargePercent { get; set; } = 50m;

    /// <summary>Attesa minima dopo un ciclo precedente, per evitare oscillazioni (R7).</summary>
    public int AntiFlappingCooldownSeconds { get; set; } = 900;

    public bool RequireManualApproval { get; set; } = true;

    public Guid? DefaultSequenceId { get; set; }
    public StartupSequence? DefaultSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
