using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Orchestrator;

/// <summary>
/// Avvia l'esecuzione di una sequenza di spegnimento quando il motore decisionale lo richiede.
/// <para>
/// L'esecuzione gira in un task indipendente dal ciclo di polling NUT: una sequenza può durare
/// minuti, e bloccare <see cref="NutPollingService"/> per tutto quel tempo significherebbe
/// smettere di osservare l'UPS esattamente mentre serve di più (§2.1).
/// </para>
/// <para>
/// <b>Un solo tentativo automatico per blackout</b>: se la sequenza fallisce, non viene
/// ritentata automaticamente finché il blackout è lo stesso. Un retry automatico di una
/// sequenza di spegnimento senza supervisione sarebbe un comportamento rischioso da introdurre
/// senza una revisione dedicata; un nuovo tentativo manuale resta possibile dal portale (Fase 3+).
/// </para>
/// </summary>
public sealed class ShutdownExecutionCoordinator(
    IServiceScopeFactory scopeFactory,
    SequenceExecutor executor,
    Core.Abstractions.IClock clock,
    IOptions<OrchestratorOptions> options,
    ILogger<ShutdownExecutionCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly ConcurrentDictionary<string, byte> _powerRestoredSignal = new();

    /// <summary>Segnala che la corrente è tornata per questo blackout (§11, caso B).</summary>
    public void SignalPowerRestored(string blackoutId) => _powerRestoredSignal[blackoutId] = 0;

    /// <summary>
    /// Tenta di avviare la sequenza per il blackout indicato. Esegue rapidamente i controlli di
    /// idempotenza e restituisce; l'esecuzione effettiva prosegue in background.
    /// </summary>
    public async Task TryStartAsync(ShutdownPolicy policy, RuleDecision decision,
        BlackoutState blackout, EvaluationContext decisionContext, CancellationToken appCt)
    {
        if (_active.ContainsKey(blackout.BlackoutId)) return;

        var sequenceId = decision.SequenceId ?? policy.DefaultSequenceId;
        if (sequenceId is null)
        {
            logger.LogError(
                "Nessuna sequenza di spegnimento associata alla policy o alla regola: " +
                "impossibile procedere per il blackout {BlackoutId}.", blackout.BlackoutId);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        // Un solo tentativo per blackout, a prescindere dall'esito: la ricerca include tutti gli
        // stati, non solo quelli attivi, perché il vincolo univoco nel database copre solo
        // Pending/Running e da solo non impedirebbe un secondo tentativo dopo un fallimento.
        var alreadyAttempted = await db.OrchestrationRuns.AnyAsync(
            r => r.BlackoutId == blackout.BlackoutId && r.RunKind == RunKind.Shutdown, appCt);
        if (alreadyAttempted) return;

        if (!_active.TryAdd(blackout.BlackoutId, 0)) return;

        var runId = Guid.CreateVersion7();
        var run = new OrchestrationRun
        {
            Id = runId,
            BlackoutId = blackout.BlackoutId,
            RunKind = RunKind.Shutdown,
            TriggerReason = decision.Reason,
            ExecutionMode = policy.ExecutionMode,
            SequenceId = sequenceId,
            State = RunState.Running,
            Owner = WorkerRole.Orchestrator,
            StartedAt = clock.UtcNow,
            RuleSnapshotJson = JsonSerializer.Serialize(new
            {
                decision.TriggeredRuleName,
                decision.Reason,
                decision.BasedOnUncertainData
            })
        };
        db.OrchestrationRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(appCt);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Un'altra istanza (o il Safety Agent dopo un takeover) ha già reclamato questo
            // blackout: non è un errore, è la mutua esclusione che funziona (§4.5, R2).
            logger.LogInformation(
                "Blackout {BlackoutId} già reclamato da un'altra esecuzione: nessuna azione.",
                blackout.BlackoutId);
            _active.TryRemove(blackout.BlackoutId, out _);
            return;
        }

        logger.LogWarning(
            "SEQUENZA AVVIATA dall'orchestrator in modalità {Mode}. Blackout {BlackoutId}. " +
            "Motivo: {Reason}", policy.ExecutionMode, blackout.BlackoutId, decision.Reason);

        _ = Task.Run(() => RunSequenceAsync(runId, policy, sequenceId.Value, decisionContext,
                blackout, appCt), CancellationToken.None)
            .ContinueWith(t =>
            {
                _active.TryRemove(blackout.BlackoutId, out _);
                _powerRestoredSignal.TryRemove(blackout.BlackoutId, out _);
                if (t.Exception is not null)
                    logger.LogError(t.Exception,
                        "Esecuzione della sequenza terminata con un'eccezione non gestita per il blackout {BlackoutId}.",
                        blackout.BlackoutId);
            }, TaskScheduler.Default);
    }

    private async Task RunSequenceAsync(Guid runId, ShutdownPolicy policy, Guid sequenceId,
        EvaluationContext decisionContext, BlackoutState blackout, CancellationToken appCt)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ISequenceRunStore>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var steps = await db.ShutdownSteps
            .AsNoTracking()
            .Where(s => s.ShutdownSequenceId == sequenceId)
            .ToListAsync(appCt);

        var groupMembers = await BuildGroupMembersAsync(db, appCt);
        var executable = SequenceStepResolver.Resolve(steps, groupMembers);

        var deviceIds = executable.SelectMany(s => s.TargetDeviceIds).Distinct().ToList();
        var devices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, appCt);

        var result = await executor.ExecuteAsync(
            runId.ToString(),
            SequenceDirection.Shutdown,
            executable,
            devices,
            store,
            // La condizione è valutata contro lo stato dell'UPS al momento della decisione: una
            // sequenza di qualche minuto non ri-legge NUT a ogni step in questa fase del
            // progetto. È una semplificazione documentata, non un'approssimazione silenziosa.
            evaluateCondition: node => ConditionEvaluator.Evaluate(node, decisionContext),
            shouldStopAtBoundary: () => ShouldStopAtBoundary(policy, blackout.BlackoutId),
            diagnostics: (severity, message) => Log(severity, message),
            ct: appCt);

        var run = await db.OrchestrationRuns.FirstAsync(r => r.Id == runId, appCt);
        run.State = result.FinalState;
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(appCt);

        await audit.RecordAsync(new AuditEntry
        {
            Action = "Orchestration.ShutdownSequence",
            EntityType = nameof(OrchestrationRun),
            EntityId = runId.ToString(),
            UserName = "orchestrator",
            Outcome = result.FinalState.ToString(),
            Reason = result.StopReason,
            AfterJson = JsonSerializer.Serialize(new
            {
                result.FinalState,
                result.StoppedAtBoundary,
                actionCount = result.Actions.Count,
                failedCount = result.Actions.Count(a => a.State is ActionState.Failed or ActionState.TimedOut)
            })
        }, appCt);
        await db.SaveChangesAsync(appCt);

        logger.LogWarning(
            "Sequenza {RunId} per il blackout {BlackoutId} terminata con stato {State} " +
            "({ActionCount} azioni registrate).",
            runId, blackout.BlackoutId, result.FinalState, result.Actions.Count);
    }

    /// <summary>
    /// Decide se interrompere al confine fra step. Il kill switch ha sempre la precedenza; il
    /// ritorno della corrente interrompe solo se la policy lo prevede esplicitamente (§11, caso B).
    /// </summary>
    private bool ShouldStopAtBoundary(ShutdownPolicy policy, string blackoutId)
    {
        if (File.Exists(options.Value.KillSwitchPath))
        {
            logger.LogWarning(
                "Kill switch attivo durante l'esecuzione: sequenza interrotta al prossimo confine.");
            return true;
        }

        return policy.PowerRestoredDuringShutdownPolicy == PowerRestoredDuringShutdownPolicy.StopAtNextStepBoundary
               && _powerRestoredSignal.ContainsKey(blackoutId);
    }

    private void Log(EventSeverity severity, string message)
    {
        switch (severity)
        {
            case EventSeverity.Critical: logger.LogError(message); break;
            case EventSeverity.Warning: logger.LogWarning(message); break;
            default: logger.LogInformation(message); break;
        }
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<Guid>>> BuildGroupMembersAsync(
        UpsDbContext db, CancellationToken ct)
    {
        var devices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => d.DeviceGroupId != null)
            .Select(d => new { d.Id, d.DeviceGroupId })
            .ToListAsync(ct);

        return devices
            .GroupBy(d => d.DeviceGroupId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(d => d.Id).ToList());
    }
}
