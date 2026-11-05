using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Contracts.Dto;

public enum SequenceKind
{
    Shutdown = 0,
    Startup = 1
}

public sealed record SequenceListItemDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool IsEnabled { get; init; }
    public required int StepCount { get; init; }
}

public sealed record SequenceHeaderFormDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Rappresentazione normalizzata di uno step, usata dall'editor per entrambe le direzioni
/// (spegnimento e accensione): <see cref="Entities.ShutdownStep"/> e
/// <see cref="Entities.StartupStep"/> non condividono un tipo base, ma l'editor sì.
/// </summary>
public sealed record SequenceStepDto
{
    public Guid? Id { get; init; }
    public required int OrderIndex { get; init; }
    public required string Name { get; init; }
    public required SequenceStepType StepType { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public string? TargetDeviceName { get; init; }
    public Guid? TargetGroupId { get; init; }
    public string? TargetGroupName { get; init; }
    public int? ParallelGroup { get; init; }
    public int DelayBeforeSeconds { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxRetries { get; init; }
    public bool ContinueOnError { get; init; }
    public bool VerifyCompletion { get; init; } = true;
    public IReadOnlyList<Guid> ExcludedDeviceIds { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
}

public sealed record DependencyValidationResultDto
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Ordine di spegnimento risultante, per nome dispositivo (diagnostica per l'utente).</summary>
    public IReadOnlyList<string> ShutdownOrderDeviceNames { get; init; } = [];
}
