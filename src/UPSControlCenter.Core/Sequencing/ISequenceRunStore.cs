using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Sequencing;

/// <summary>
/// Persistenza dell'idempotenza e dello stato di una sequenza in esecuzione.
/// <para>
/// Esistono due implementazioni concrete, una su PostgreSQL (Orchestrator) e una su SQLite
/// (Safety Agent), più una in memoria per il Simulatore e i test. Il contratto è lo stesso
/// in tutti i casi: l'unicità logica <c>(runId, stepId, deviceId, attempt)</c> deve essere
/// garantita dalla persistenza sottostante, non dal chiamante (§4.5, §13).
/// </para>
/// </summary>
public interface ISequenceRunStore
{
    /// <summary>
    /// Vero se questa azione logica è già stata completata con successo in una esecuzione
    /// precedente (riavvio, retry esterno): l'esecutore la salta invece di ripeterla.
    /// </summary>
    Task<bool> IsStepAlreadyDoneAsync(string runId, Guid stepId, Guid? deviceId,
        CancellationToken ct = default);

    /// <summary>
    /// Tenta di "prenotare" l'esecuzione di un tentativo. Restituisce <c>false</c> se la stessa
    /// chiave logica è già registrata (da questo o da un altro processo concorrente): in tal
    /// caso l'esecutore non deve procedere con l'azione.
    /// </summary>
    Task<bool> TryBeginActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        string actionType, CancellationToken ct = default);

    /// <summary>Registra l'esito di un'azione già prenotata con <see cref="TryBeginActionAsync"/>.</summary>
    Task CompleteActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        ActionState state, string? resultCode, string? message, bool wasDryRun,
        CancellationToken ct = default);
}

/// <summary>
/// Implementazione in memoria, non persistente: usata dal Simulatore (dove ogni esecuzione è
/// isolata e non deve sopravvivere a un riavvio) e dai test unitari del motore.
/// </summary>
public sealed class InMemorySequenceRunStore : ISequenceRunStore
{
    private readonly HashSet<string> _completed = [];
    private readonly HashSet<string> _claimed = [];

    private static string Key(string runId, Guid stepId, Guid? deviceId, int attempt) =>
        $"{runId}:{stepId}:{deviceId}:{attempt}";

    public Task<bool> IsStepAlreadyDoneAsync(string runId, Guid stepId, Guid? deviceId,
        CancellationToken ct = default)
    {
        var prefix = $"{runId}:{stepId}:{deviceId}:";
        return Task.FromResult(_completed.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)));
    }

    public Task<bool> TryBeginActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        string actionType, CancellationToken ct = default)
    {
        var key = Key(runId, stepId, deviceId, attempt);
        return Task.FromResult(_claimed.Add(key));
    }

    public Task CompleteActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        ActionState state, string? resultCode, string? message, bool wasDryRun,
        CancellationToken ct = default)
    {
        if (state is ActionState.Succeeded or ActionState.Skipped)
            _completed.Add(Key(runId, stepId, deviceId, attempt));

        return Task.CompletedTask;
    }
}
