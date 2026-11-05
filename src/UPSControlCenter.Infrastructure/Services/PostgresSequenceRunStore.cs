using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Infrastructure.Services;

/// <summary>
/// Implementazione di <see cref="ISequenceRunStore"/> su PostgreSQL, usata dall'Orchestrator.
/// <para>
/// L'idempotenza non è un controllo applicativo: è il vincolo univoco
/// <c>ux_orchestration_actions_logical</c> su <c>(OrchestrationRunId, StepId, DeviceId, Attempt)</c>,
/// verificato nel database reale da <c>PostgresConstraintTests</c> (§4.5, R2). Questa classe si
/// limita a intercettare la violazione e tradurla in "azione già prenotata".
/// </para>
/// </summary>
public sealed class PostgresSequenceRunStore(
    IDbContextFactory<UpsDbContext> dbContextFactory,
    IClock clock,
    ILogger<PostgresSequenceRunStore> logger) : ISequenceRunStore
{
    public async Task<bool> IsStepAlreadyDoneAsync(string runId, Guid stepId, Guid? deviceId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var runGuid)) return false;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        return await db.OrchestrationActions
            .AsNoTracking()
            .AnyAsync(a => a.OrchestrationRunId == runGuid && a.StepId == stepId
                        && a.DeviceId == deviceId
                        && (a.State == ActionState.Succeeded || a.State == ActionState.Skipped), ct);
    }

    public async Task<bool> TryBeginActionAsync(string runId, Guid stepId, Guid? deviceId,
        int attempt, string actionType, CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var runGuid))
        {
            logger.LogError("RunId «{RunId}» non è un GUID valido: impossibile prenotare l'azione.", runId);
            return false;
        }

        // Un contesto dedicato per questa singola INSERT: evita che un conflitto di chiave
        // sporchi il ChangeTracker di un contesto condiviso, cosa rilevante perché step dello
        // stesso gruppo parallelo possono prenotare azioni concorrentemente.
        await using var scoped = await dbContextFactory.CreateDbContextAsync(ct);

        scoped.OrchestrationActions.Add(new OrchestrationAction
        {
            OrchestrationRunId = runGuid,
            StepId = stepId,
            DeviceId = deviceId,
            Attempt = attempt,
            ActionType = actionType,
            State = ActionState.Running,
            StartedAt = clock.UtcNow,
            AdapterName = "pending"
        });

        try
        {
            await scoped.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation(
                "Azione già prenotata (run {Run}, step {Step}, dispositivo {Device}, " +
                "tentativo {Attempt}): claim respinto dal database.",
                runId, stepId, deviceId, attempt);
            return false;
        }
    }

    public async Task CompleteActionAsync(string runId, Guid stepId, Guid? deviceId, int attempt,
        ActionState state, string? resultCode, string? message, bool wasDryRun,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(runId, out var runGuid)) return;

        await using var scoped = await dbContextFactory.CreateDbContextAsync(ct);

        var action = await scoped.OrchestrationActions.FirstOrDefaultAsync(a =>
            a.OrchestrationRunId == runGuid && a.StepId == stepId && a.DeviceId == deviceId
            && a.Attempt == attempt, ct);

        if (action is null)
        {
            logger.LogWarning(
                "Nessuna azione prenotata trovata per run {Run}, step {Step}, dispositivo " +
                "{Device}, tentativo {Attempt}: impossibile registrare l'esito.",
                runId, stepId, deviceId, attempt);
            return;
        }

        action.State = state;
        action.FinishedAt = clock.UtcNow;
        action.ResultCode = resultCode;
        action.Message = message;
        action.WasDryRun = wasDryRun;
        action.AdapterName = wasDryRun ? UPSControlCenter.Adapters.DryRunDeviceAdapter.Name : "live";

        await scoped.SaveChangesAsync(ct);
    }
}
