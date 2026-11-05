using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.Infrastructure.Sqlite;

/// <summary>
/// Adatta <see cref="SafetyStore"/> al contratto <see cref="ISequenceRunStore"/> richiesto da
/// <see cref="SequenceExecutor"/>. Il Safety Agent esegue quindi le sequenze con lo stesso
/// motore dell'Orchestrator, con l'idempotenza garantita dal vincolo di unicità SQLite (§5.2).
/// </summary>
public sealed class SqliteSequenceRunStore(SafetyStore store, Core.Abstractions.IClock clock)
    : ISequenceRunStore
{
    public Task<bool> IsStepAlreadyDoneAsync(string runId, Guid stepId, Guid? deviceId,
        CancellationToken ct = default) =>
        Task.FromResult(store.IsOperationCompleted(runId, stepId.ToString(), deviceId?.ToString()));

    public Task<bool> TryBeginActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        string actionType, CancellationToken ct = default) =>
        Task.FromResult(store.TryClaimOperation(runId, stepId.ToString(), deviceId?.ToString(),
            attempt, actionType, clock.UtcNow));

    public Task CompleteActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        ActionState state, string? resultCode, string? message, bool wasDryRun,
        CancellationToken ct = default)
    {
        store.UpdateOperationResult(runId, stepId.ToString(), deviceId?.ToString(), attempt,
            state, resultCode, message, wasDryRun, clock.UtcNow);
        return Task.CompletedTask;
    }
}
