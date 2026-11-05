using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Orchestrator;

/// <summary>
/// Avvia la sequenza di riaccensione dopo un blackout, gestendo i casi C e D di §11.
/// <para>
/// <b>Caso C</b> — corrente ripristinata dopo lo spegnimento: la riaccensione parte solo dopo
/// <see cref="StartupPolicy.StabilitySamples"/> letture OL consecutive <b>e</b>
/// <see cref="StartupPolicy.RequiredStablePowerSeconds"/> di stabilità: una singola lettura non
/// basta (§12). Se <see cref="StartupPolicy.RequireManualApproval"/> è vero, la run resta in
/// stato <c>Pending</c> finché un amministratore non la approva dal portale.
/// </para>
/// <para>
/// <b>Caso D</b> — corrente nuovamente assente durante la riaccensione: l'esecuzione si
/// interrompe al confine fra step <b>a prescindere dalla policy</b>. A differenza del caso B
/// (ritorno corrente durante lo spegnimento, dove la scelta è dell'operatore), continuare ad
/// accendere dispositivi senza alimentazione di rete non è mai la scelta giusta: i dispositivi
/// già accesi non vengono toccati, quelli non ancora avviati restano spenti.
/// </para>
/// </summary>
public sealed class StartupExecutionCoordinator(
    IServiceScopeFactory scopeFactory,
    SequenceExecutor executor,
    IClock clock,
    IOptions<OrchestratorOptions> options,
    ILogger<StartupExecutionCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastCompletedPerUps = new();

    /// <summary>
    /// Vero quando almeno un UPS è attualmente su batteria. Aggiornato dal polling a ogni
    /// ciclo: una nuova interruzione interrompe qualunque riaccensione in corso, su qualunque
    /// UPS (semplificazione conservativa valida con uno o più UPS, §11 caso D).
    /// </summary>
    public bool AnyUpsOnBattery { get; set; }

    /// <summary>
    /// Valuta se avviare la riaccensione per il blackout appena chiuso. Va chiamato a ogni
    /// ciclo di polling dopo che un blackout si è concluso: i controlli di stabilità richiedono
    /// osservazioni ripetute, non un singolo evento.
    /// </summary>
    public async Task EvaluateAsync(UpsDevice ups, StartupPolicy policy, BlackoutState closedBlackout,
        int consecutiveOnLineSamples, UpsSnapshot snapshot, CancellationToken appCt)
    {
        if (_active.ContainsKey(closedBlackout.BlackoutId)) return;

        if (consecutiveOnLineSamples < policy.StabilitySamples) return;

        // Stabilità misurata sul numero di letture concordi, non sul tempo di orologio da solo:
        // con un intervallo di polling noto, N letture concordi implicano almeno N*intervallo di
        // stabilità, che è la grandezza che vogliamo garantire (§12).
        var pollingInterval = TimeSpan.FromSeconds(ups.PollingIntervalSeconds);
        var observedStableDuration = pollingInterval * consecutiveOnLineSamples;
        if (observedStableDuration < TimeSpan.FromSeconds(policy.RequiredStablePowerSeconds)) return;

        // Carica sconosciuta: scelta conservativa, non si riaccende (§9.3 applicato per analogia
        // alla riaccensione: un valore mancante non autorizza un'azione che richiede quel dato).
        if (snapshot.BatteryChargePercent is not { } charge || charge < policy.MinBatteryChargePercent)
        {
            logger.LogInformation(
                "Riaccensione non avviata per il blackout {BlackoutId}: carica batteria {Charge} " +
                "sotto la soglia {Min}% o non disponibile.",
                closedBlackout.BlackoutId, snapshot.BatteryChargePercent, policy.MinBatteryChargePercent);
            return;
        }

        if (_lastCompletedPerUps.TryGetValue(ups.Id, out var lastCompleted)
            && clock.UtcNow - lastCompleted < TimeSpan.FromSeconds(policy.AntiFlappingCooldownSeconds))
        {
            logger.LogInformation(
                "Riaccensione non avviata per «{Ups}»: cooldown anti-flapping attivo ({Remaining:F0}s residui).",
                ups.Name, policy.AntiFlappingCooldownSeconds - (clock.UtcNow - lastCompleted).TotalSeconds);
            return;
        }

        if (policy.DefaultSequenceId is null)
        {
            logger.LogError(
                "Nessuna sequenza di riaccensione configurata: impossibile procedere per il " +
                "blackout {BlackoutId}.", closedBlackout.BlackoutId);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        var alreadyAttempted = await db.OrchestrationRuns.AnyAsync(
            r => r.BlackoutId == closedBlackout.BlackoutId && r.RunKind == RunKind.Startup, appCt);
        if (alreadyAttempted) return;

        if (!_active.TryAdd(closedBlackout.BlackoutId, 0)) return;

        var runId = Guid.CreateVersion7();
        var requiresApproval = policy.RequireManualApproval;

        var run = new OrchestrationRun
        {
            Id = runId,
            BlackoutId = closedBlackout.BlackoutId,
            RunKind = RunKind.Startup,
            TriggerReason = $"Alimentazione stabile per {observedStableDuration.TotalSeconds:F0}s " +
                           $"({consecutiveOnLineSamples} letture), carica {charge}%.",
            ExecutionMode = policy.ExecutionMode,
            SequenceId = policy.DefaultSequenceId,
            State = requiresApproval ? RunState.Pending : RunState.Running,
            Owner = WorkerRole.Orchestrator,
            StartedAt = clock.UtcNow
        };
        db.OrchestrationRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(appCt);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            _active.TryRemove(closedBlackout.BlackoutId, out _);
            return;
        }

        if (requiresApproval)
        {
            logger.LogWarning(
                "Riaccensione pronta per il blackout {BlackoutId}: in attesa di approvazione " +
                "manuale (run {RunId}).", closedBlackout.BlackoutId, runId);
            // Non è un'esecuzione attiva: si libera la chiave locale, la riprenderà ApproveAsync.
            _active.TryRemove(closedBlackout.BlackoutId, out _);
            return;
        }

        logger.LogWarning(
            "RIACCENSIONE AVVIATA dall'orchestrator in modalità {Mode} per il blackout {BlackoutId}.",
            policy.ExecutionMode, closedBlackout.BlackoutId);

        StartExecution(runId, ups.Id, policy.DefaultSequenceId.Value, closedBlackout.BlackoutId, appCt);
    }

    /// <summary>Approva ed esegue una riaccensione in attesa (§12, approvazione manuale).</summary>
    public async Task<bool> ApproveAsync(Guid runId, Guid upsId, CancellationToken appCt)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        var run = await db.OrchestrationRuns.FirstOrDefaultAsync(
            r => r.Id == runId && r.RunKind == RunKind.Startup && r.State == RunState.Pending, appCt);

        if (run is null || run.BlackoutId is null || run.SequenceId is null) return false;
        if (!_active.TryAdd(run.BlackoutId, 0)) return false;

        run.State = RunState.Running;
        await db.SaveChangesAsync(appCt);

        logger.LogWarning("Riaccensione {RunId} approvata ed avviata per il blackout {BlackoutId}.",
            runId, run.BlackoutId);

        StartExecution(runId, upsId, run.SequenceId.Value, run.BlackoutId, appCt);
        return true;
    }

    private void StartExecution(Guid runId, Guid upsId, Guid sequenceId, string blackoutId,
        CancellationToken appCt)
    {
        _ = Task.Run(() => RunSequenceAsync(runId, sequenceId, blackoutId, appCt), CancellationToken.None)
            .ContinueWith(t =>
            {
                _active.TryRemove(blackoutId, out _);
                if (t.IsCompletedSuccessfully && t.Result)
                    _lastCompletedPerUps[upsId] = clock.UtcNow;
                if (t.Exception is not null)
                    logger.LogError(t.Exception,
                        "Esecuzione della riaccensione terminata con un'eccezione non gestita per il blackout {BlackoutId}.",
                        blackoutId);
            }, TaskScheduler.Default);
    }

    private async Task<bool> RunSequenceAsync(Guid runId, Guid sequenceId, string blackoutId,
        CancellationToken appCt)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ISequenceRunStore>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var steps = await db.StartupSteps
            .AsNoTracking()
            .Where(s => s.StartupSequenceId == sequenceId)
            .ToListAsync(appCt);

        var groupedDevices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => d.DeviceGroupId != null)
            .Select(d => new { d.Id, d.DeviceGroupId })
            .ToListAsync(appCt);

        var groupMembers = groupedDevices
            .GroupBy(d => d.DeviceGroupId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(d => d.Id).ToList());

        var executable = SequenceStepResolver.Resolve(steps, groupMembers);

        var deviceIds = executable.SelectMany(s => s.TargetDeviceIds).Distinct().ToList();
        var devices = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, appCt);

        var result = await executor.ExecuteAsync(
            runId.ToString(),
            SequenceDirection.Startup,
            executable,
            devices,
            store,
            shouldStopAtBoundary: () => ShouldStopAtBoundary(),
            diagnostics: (severity, message) => Log(severity, message),
            ct: appCt);

        var run = await db.OrchestrationRuns.FirstAsync(r => r.Id == runId, appCt);
        run.State = result.FinalState;
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(appCt);

        await audit.RecordAsync(new AuditEntry
        {
            Action = "Orchestration.StartupSequence",
            EntityType = nameof(OrchestrationRun),
            EntityId = runId.ToString(),
            UserName = "orchestrator",
            Outcome = result.FinalState.ToString(),
            Reason = result.StopReason,
            AfterJson = JsonSerializer.Serialize(new
            {
                result.FinalState,
                result.StoppedAtBoundary,
                actionCount = result.Actions.Count
            })
        }, appCt);
        await db.SaveChangesAsync(appCt);

        logger.LogWarning(
            "Riaccensione {RunId} per il blackout {BlackoutId} terminata con stato {State}.",
            runId, blackoutId, result.FinalState);

        return result.FinalState == RunState.Completed;
    }

    /// <summary>
    /// Il kill switch e una nuova interruzione interrompono sempre, a prescindere dalla policy
    /// (§11, caso D): non ha senso continuare ad accendere dispositivi senza corrente.
    /// </summary>
    private bool ShouldStopAtBoundary()
    {
        if (File.Exists(options.Value.KillSwitchPath)) return true;
        return AnyUpsOnBattery;
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
}
