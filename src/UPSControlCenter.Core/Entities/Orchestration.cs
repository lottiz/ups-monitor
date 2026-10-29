using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

/// <summary>
/// Esecuzione di una sequenza. Un indice univoco parziale su
/// <c>(BlackoutId, RunKind)</c> limitato agli stati attivi impedisce a livello di database
/// due sequenze concorrenti per lo stesso blackout (§4.4).
/// </summary>
public class OrchestrationRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Identificativo deterministico del blackout, condiviso con il Safety Agent (§1.4).</summary>
    public string? BlackoutId { get; set; }

    public RunKind RunKind { get; set; }
    public string TriggerReason { get; set; } = string.Empty;
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.DryRun;

    public Guid? SequenceId { get; set; }
    public RunState State { get; set; } = RunState.Pending;

    public WorkerRole Owner { get; set; } = WorkerRole.Orchestrator;

    /// <summary>Token di fencing: azioni con token inferiore all'attuale vengono rifiutate.</summary>
    public long FencingToken { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Copia della regola e dello stato UPS al momento della decisione, per l'audit.</summary>
    public string? RuleSnapshotJson { get; set; }

    public ICollection<OrchestrationAction> Actions { get; set; } = [];
}

/// <summary>
/// Singola azione eseguita. L'unicità di <c>(RunId, StepId, DeviceId, Attempt)</c> è il
/// meccanismo di idempotenza: una seconda esecuzione viola il vincolo e viene scartata (§4.5).
/// </summary>
public class OrchestrationAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OrchestrationRunId { get; set; }
    public OrchestrationRun? OrchestrationRun { get; set; }

    public Guid StepId { get; set; }
    public Guid? DeviceId { get; set; }
    public int Attempt { get; set; } = 1;

    public string ActionType { get; set; } = string.Empty;
    public ActionState State { get; set; } = ActionState.Pending;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public string? ResultCode { get; set; }
    public string? Message { get; set; }
    public string AdapterName { get; set; } = string.Empty;

    /// <summary>Tracciabilità esplicita: distingue le azioni simulate da quelle reali.</summary>
    public bool WasDryRun { get; set; } = true;
}

public class SimulationRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string ScenarioJson { get; set; } = "{}";
    public string? TimelineJson { get; set; }
    public RunState State { get; set; } = RunState.Pending;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int SimulatedDurationSeconds { get; set; }
    public string? Summary { get; set; }
}

/// <summary>Heartbeat dei worker: base del meccanismo di takeover (§1.4).</summary>
public class WorkerHeartbeat
{
    public string WorkerName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
    public string State { get; set; } = string.Empty;
    public long FencingToken { get; set; }
    public string? DetailsJson { get; set; }
}

/// <summary>
/// Versione di configurazione distribuita al Safety Agent. Un indice univoco parziale garantisce
/// una sola versione in stato <c>Published</c> (§4.4).
/// </summary>
public class ConfigurationVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Numero monotono crescente. Il rollback pubblica un payload storico con versione nuova (§7.4).</summary>
    public long Version { get; set; }

    public string PayloadJson { get; set; } = "{}";
    public string ChecksumSha256 { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 del payload con chiave dedicata: autentica l'origine della configurazione.</summary>
    public byte[] Signature { get; set; } = [];

    public ConfigurationState State { get; set; } = ConfigurationState.Draft;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public string? ActivatedByAgent { get; set; }
    public string? ValidationErrorsJson { get; set; }
}
