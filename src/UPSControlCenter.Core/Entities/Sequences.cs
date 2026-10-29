using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

public class ShutdownSequence
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ShutdownStep> Steps { get; set; } = [];
}

/// <summary>Fase di una sequenza di spegnimento. L'ordine è configurabile (§10).</summary>
public class ShutdownStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShutdownSequenceId { get; set; }
    public ShutdownSequence? ShutdownSequence { get; set; }

    public int OrderIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public SequenceStepType StepType { get; set; }

    public Guid? TargetDeviceId { get; set; }
    public ManagedDevice? TargetDevice { get; set; }
    public Guid? TargetGroupId { get; set; }
    public DeviceGroup? TargetGroup { get; set; }

    /// <summary>Step con lo stesso valore non nullo vengono eseguiti in parallelo.</summary>
    public int? ParallelGroup { get; set; }

    public int DelayBeforeSeconds { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; }
    public bool ContinueOnError { get; set; }

    /// <summary>Verifica obbligatoria del completamento prima di procedere (§10).</summary>
    public bool VerifyCompletion { get; set; } = true;

    public string? ConditionJson { get; set; }

    /// <summary>Dispositivi esclusi da questo step, anche se appartengono al gruppo bersaglio.</summary>
    public List<Guid> ExcludedDeviceIds { get; set; } = [];

    public bool IsEnabled { get; set; } = true;
}

public class StartupSequence
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<StartupStep> Steps { get; set; } = [];
}

public class StartupStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid StartupSequenceId { get; set; }
    public StartupSequence? StartupSequence { get; set; }

    public int OrderIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public SequenceStepType StepType { get; set; }

    public Guid? TargetDeviceId { get; set; }
    public ManagedDevice? TargetDevice { get; set; }
    public Guid? TargetGroupId { get; set; }
    public DeviceGroup? TargetGroup { get; set; }

    public int? ParallelGroup { get; set; }
    public int DelayBeforeSeconds { get; set; }
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRetries { get; set; }
    public bool ContinueOnError { get; set; }

    /// <summary>Attende che il servizio sia effettivamente disponibile, non solo che risponda al ping.</summary>
    public bool VerifyCompletion { get; set; } = true;

    public string? ConditionJson { get; set; }
    public List<Guid> ExcludedDeviceIds { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
}
