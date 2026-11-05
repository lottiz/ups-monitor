using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Sequencing;

/// <summary>Direzione di una sequenza: determina quale azione dell'adapter viene invocata.</summary>
public enum SequenceDirection
{
    Shutdown = 0,
    Startup = 1
}

/// <summary>
/// Rappresentazione neutra di uno step, indipendente da <see cref="Entities.ShutdownStep"/> o
/// <see cref="Entities.StartupStep"/>. Permette a <see cref="SequenceExecutor"/> di restare un
/// unico motore condiviso da Orchestrator, Safety Agent e Simulatore, ciascuno con la propria
/// fonte dati (EF Core, <c>SafetyConfiguration</c>, scenario di test) che si limita a produrre
/// questa struttura (§2.1, §14).
/// </summary>
public sealed record ExecutableStep
{
    public required Guid Id { get; init; }
    public required int OrderIndex { get; init; }
    public required string Name { get; init; }
    public required SequenceStepType StepType { get; init; }

    /// <summary>Dispositivi bersaglio, già risolti (gruppo espanso, esclusioni applicate).</summary>
    public IReadOnlyList<Guid> TargetDeviceIds { get; init; } = [];

    /// <summary>Step con lo stesso valore non nullo vengono eseguiti in parallelo.</summary>
    public int? ParallelGroup { get; init; }

    public int DelayBeforeSeconds { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxRetries { get; init; }
    public bool ContinueOnError { get; init; }
    public bool VerifyCompletion { get; init; } = true;

    /// <summary>Condizione da valutare prima di eseguire lo step. Null = sempre eseguito.</summary>
    public Rules.ConditionNode? Condition { get; init; }
}

/// <summary>Esito dell'esecuzione (o simulazione) di un singolo step su un singolo dispositivo.</summary>
public sealed record StepActionOutcome
{
    public required Guid StepId { get; init; }
    public Guid? DeviceId { get; init; }
    public required int Attempt { get; init; }
    public required ActionState State { get; init; }
    public string? ResultCode { get; init; }
    public string? Message { get; init; }
    public required bool WasDryRun { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Esito complessivo dell'esecuzione di una sequenza.</summary>
public sealed record SequenceExecutionResult
{
    public required RunState FinalState { get; init; }
    public required IReadOnlyList<StepActionOutcome> Actions { get; init; }

    /// <summary>
    /// Vero se l'esecuzione si è interrotta al confine fra due step per un segnale esterno
    /// (es. ritorno della corrente, caso B di §11), e non per un errore.
    /// </summary>
    public bool StoppedAtBoundary { get; init; }

    public string? StopReason { get; init; }
}
