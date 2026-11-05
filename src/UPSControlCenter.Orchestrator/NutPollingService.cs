using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Nut;

namespace UPSControlCenter.Orchestrator;

/// <summary>
/// Monitoraggio NUT e valutazione delle regole lato Control Plane (§2.1).
/// <para>
/// Usa gli stessi <see cref="RuleEvaluator"/> e <see cref="BlackoutTracker"/> del Safety Agent:
/// l'orchestrator è il percorso normale, l'agent è la rete di sicurezza, ma la decisione è
/// calcolata dallo stesso codice.
/// </para>
/// </summary>
public sealed class NutPollingService(
    IServiceScopeFactory scopeFactory,
    INutClient nutClient,
    IClock clock,
    IOptions<OrchestratorOptions> options,
    IOptions<NutClientOptions> nutOptions,
    ShutdownExecutionCoordinator shutdownCoordinator,
    StartupExecutionCoordinator startupCoordinator,
    ILogger<NutPollingService> logger) : BackgroundService
{
    private readonly OrchestratorOptions _options = options.Value;
    private readonly RuleEvaluator _evaluator = new();
    private readonly Dictionary<Guid, BlackoutTracker> _trackers = [];
    private readonly string _bootId = $"orchestrator-{Guid.CreateVersion7()}";
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Ultima decisione per UPS, esposta alla dashboard tramite lo stato condiviso.</summary>
    public IReadOnlyDictionary<Guid, RuleDecision> LastDecisions => _lastDecisions;
    private readonly Dictionary<Guid, RuleDecision> _lastDecisions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Orchestrator avviato. Istanza {Instance}.", _instanceId);

        // Il database può non essere ancora pronto all'avvio del container: si attende senza
        // fallire, perché il restart loop di Docker non aiuterebbe.
        await WaitForDatabaseAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = nutOptions.Value.PollingIntervalSeconds;

            try
            {
                interval = await PollOnceAsync(stoppingToken);
                TouchHealthSentinel();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Il loop di monitoraggio non deve terminare: la perdita dell'orchestrator
                // trasferirebbe la responsabilità al Safety Agent, che è una degradazione evitabile.
                logger.LogError(ex, "Errore nel ciclo di polling. Si riprova al ciclo successivo.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, interval)), stoppingToken);
        }
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
                await db.Database.CanConnectAsync(ct);
                if (await db.UpsDevices.AnyAsync(ct) || attempt > 0)
                {
                    logger.LogInformation("Connessione a PostgreSQL verificata.");
                    return;
                }
                return;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(60, 2 * attempt));
                logger.LogWarning(
                    "PostgreSQL non disponibile (tentativo {Attempt}): nuovo tentativo in {Delay}s.",
                    attempt, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Esegue un ciclo completo su tutti gli UPS abilitati. Restituisce l'intervallo del prossimo ciclo.</summary>
    private async Task<int> PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        var upsDevices = await db.UpsDevices.Where(u => u.IsEnabled).ToListAsync(ct);
        if (upsDevices.Count == 0)
        {
            logger.LogWarning("Nessun UPS abilitato in configurazione: nulla da monitorare.");
            return 30;
        }

        var policy = await db.ShutdownPolicies
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.IsActive, ct);

        var startupPolicy = await db.StartupPolicies.FirstOrDefaultAsync(p => p.IsActive, ct);

        var automationDisabled = File.Exists(_options.KillSwitchPath);
        var minInterval = int.MaxValue;
        var anyOnBattery = false;

        foreach (var ups in upsDevices)
        {
            ct.ThrowIfCancellationRequested();
            minInterval = Math.Min(minInterval, ups.PollingIntervalSeconds);

            var snapshot = await ReadAsync(ups, ct);
            var tracker = GetTracker(ups, policy);
            var outcome = tracker.Process(snapshot);

            // Il segnale di ritorno della corrente viene inviato al coordinatore a prescindere
            // dal fatto che una sequenza sia in corso: è innocuo se non c'è nulla da fermare, ed
            // è l'unico modo per il caso B (§11) di sapere che la corrente è tornata senza dover
            // interrogare di nuovo NUT da dentro l'esecuzione della sequenza.
            if (outcome.BlackoutJustEnded && outcome.Blackout is not null)
                shutdownCoordinator.SignalPowerRestored(outcome.Blackout.BlackoutId);

            if (outcome.DebouncedStatus.IsOnBattery()) anyOnBattery = true;

            await PersistMeasurementAsync(db, ups, snapshot, outcome, ct);
            await PersistEventsAsync(db, ups, snapshot, outcome, ct);
            UpdateUpsIdentity(ups, snapshot);

            if (policy is not null)
            {
                var ctx = new EvaluationContext
                {
                    Snapshot = snapshot,
                    Policy = policy,
                    Blackout = outcome.Blackout,
                    DebouncedStatus = outcome.DebouncedStatus,
                    NowUtc = clock.UtcNow,
                    NowMonotonicMs = clock.MonotonicMilliseconds,
                    BootId = _bootId,
                    // L'orchestrator è se stesso: il proprio heartbeat è per definizione attuale.
                    OrchestratorHeartbeatAge = TimeSpan.Zero,
                    AutomationDisabled = automationDisabled
                };

                var decision = _evaluator.Evaluate(ctx, [.. policy.Rules]);
                _lastDecisions[ups.Id] = decision;

                if (decision.ShouldTriggerShutdown && outcome.Blackout is not null)
                {
                    logger.LogWarning(
                        "Soglia di spegnimento raggiunta per «{Ups}»: {Reason} (modalità {Mode}).",
                        ups.Name, decision.Reason, policy.ExecutionMode);

                    // Il coordinatore esegue in background: non si attende qui il completamento
                    // della sequenza, altrimenti il polling di NUT si fermerebbe per tutta la
                    // sua durata (§2.1).
                    await shutdownCoordinator.TryStartAsync(policy, decision, outcome.Blackout,
                        ctx, ct);
                }
            }

            // Caso C di §11: un blackout appena concluso è un candidato alla riaccensione. La
            // valutazione richiede letture ripetute (ConsecutiveOnLineSamples), quindi va
            // ripetuta a ogni ciclo finché la stabilità non è raggiunta o il blackout non
            // viene sostituito da uno nuovo.
            if (startupPolicy is not null && outcome.Blackout is { IsOpen: false })
            {
                await startupCoordinator.EvaluateAsync(ups, startupPolicy, outcome.Blackout,
                    outcome.ConsecutiveOnLineSamples, snapshot, ct);
            }
        }

        // Caso D di §11: una riaccensione in corso deve interrompersi se un UPS qualunque torna
        // su batteria, a prescindere da quale UPS l'avesse innescata (semplificazione
        // conservativa, valida anche con un solo UPS come nell'installazione attuale).
        startupCoordinator.AnyUpsOnBattery = anyOnBattery;

        // Riaccensioni approvate manualmente dal portale (§12): Web e Orchestrator sono processi
        // distinti che comunicano solo tramite il database, quindi l'approvazione è un campo
        // persistito che l'orchestrator rileva al ciclo successivo, non una chiamata diretta.
        var approvedPending = await db.OrchestrationRuns
            .Where(r => r.RunKind == RunKind.Startup && r.State == RunState.Pending
                     && r.ApprovedAt != null)
            .ToListAsync(ct);

        foreach (var run in approvedPending)
            await startupCoordinator.ApproveAsync(run.Id, upsDevices[0].Id, ct);

        await UpdateHeartbeatAsync(db, ct);
        await db.SaveChangesAsync(ct);

        return minInterval == int.MaxValue ? 10 : minInterval;
    }

    private async Task<UpsSnapshot> ReadAsync(UpsDevice ups, CancellationToken ct)
    {
        var snapshot = await nutClient.ReadAsync(ups.NutUpsName, ct);

        if (snapshot.CommunicationOk
            && clock.UtcNow - snapshot.AcquiredAt > TimeSpan.FromSeconds(ups.StaleDataThresholdSeconds))
        {
            return snapshot with { IsStale = true };
        }

        return snapshot;
    }

    private BlackoutTracker GetTracker(UpsDevice ups, ShutdownPolicy? policy)
    {
        if (_trackers.TryGetValue(ups.Id, out var existing)) return existing;

        var tracker = new BlackoutTracker(ups.NutUpsName, clock, _bootId,
            policy?.DebounceSamples ?? 3);
        _trackers[ups.Id] = tracker;
        return tracker;
    }

    private async Task PersistMeasurementAsync(UpsDbContext db, UpsDevice ups,
        UpsSnapshot snapshot, TrackerOutcome outcome, CancellationToken ct)
    {
        db.UpsMeasurements.Add(new UpsMeasurement
        {
            UpsDeviceId = ups.Id,
            RecordedAt = snapshot.AcquiredAt,
            StatusRaw = snapshot.StatusRaw,
            StatusFlags = (int)outcome.DebouncedStatus,
            // I valori nulli restano nulli: il database distingue "non disponibile" da zero (§4.1).
            BatteryChargePercent = snapshot.BatteryChargePercent,
            BatteryRuntimeSeconds = snapshot.BatteryRuntimeSeconds,
            BatteryVoltage = snapshot.BatteryVoltage,
            InputVoltage = snapshot.InputVoltage,
            OutputVoltage = snapshot.OutputVoltage,
            InputFrequency = snapshot.InputFrequency,
            OutputFrequency = snapshot.OutputFrequency,
            LoadPercent = snapshot.LoadPercent,
            RealPowerWatts = snapshot.RealPowerWatts,
            PhaseDataJson = snapshot.PhaseData.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(snapshot.PhaseData)
                : null,
            IsStale = snapshot.IsStale,
            Source = WorkerRole.Orchestrator
        });

        await Task.CompletedTask;
    }

    private async Task PersistEventsAsync(UpsDbContext db, UpsDevice ups, UpsSnapshot snapshot,
        TrackerOutcome outcome, CancellationToken ct)
    {
        foreach (var evt in outcome.Events)
        {
            db.UpsEvents.Add(new UpsEvent
            {
                UpsDeviceId = ups.Id,
                EventType = evt,
                Severity = evt switch
                {
                    UpsEventType.BlackoutStarted or UpsEventType.LowBattery
                        or UpsEventType.BatteryCritical => EventSeverity.Critical,
                    UpsEventType.PowerRestored or UpsEventType.CommunicationRestored
                        => EventSeverity.Information,
                    _ => EventSeverity.Warning
                },
                BlackoutId = outcome.Blackout?.BlackoutId,
                NewStatus = outcome.DebouncedStatus.ToNutString(),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    snapshot.StatusRaw,
                    snapshot.BatteryRuntimeSeconds,
                    snapshot.BatteryChargePercent,
                    snapshot.CommunicationError
                }),
                OccurredAt = clock.UtcNow,
                DetectedBy = WorkerRole.Orchestrator
            });

            logger.LogInformation("Evento {Event} per «{Ups}» (stato {Status}).",
                evt, ups.Name, outcome.DebouncedStatus.ToNutString());
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Aggiorna produttore, modello e seriale con i valori letti da NUT. Non vengono mai
    /// sovrascritti con valori nulli: un dato già noto è preferibile a un campo vuoto (§5.1).
    /// </summary>
    private static void UpdateUpsIdentity(UpsDevice ups, UpsSnapshot snapshot)
    {
        if (!snapshot.CommunicationOk) return;

        var changed = false;
        if (snapshot.Manufacturer is { } mfr && ups.Manufacturer != mfr) { ups.Manufacturer = mfr; changed = true; }
        if (snapshot.Model is { } model && ups.Model != model) { ups.Model = model; changed = true; }
        if (snapshot.Serial is { } serial && ups.Serial != serial) { ups.Serial = serial; changed = true; }
        if (snapshot.FirmwareVersion is { } fw && ups.FirmwareVersion != fw) { ups.FirmwareVersion = fw; changed = true; }

        if (changed) ups.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Registra l'heartbeat: è il segnale che impedisce al Safety Agent di eseguire il takeover
    /// quando l'orchestrator è vivo (§1.4).
    /// </summary>
    private async Task UpdateHeartbeatAsync(UpsDbContext db, CancellationToken ct)
    {
        var heartbeat = await db.WorkerHeartbeats
            .FirstOrDefaultAsync(h => h.WorkerName == _options.WorkerName, ct);

        if (heartbeat is null)
        {
            heartbeat = new WorkerHeartbeat
            {
                WorkerName = _options.WorkerName,
                InstanceId = _instanceId,
                FencingToken = 0
            };
            db.WorkerHeartbeats.Add(heartbeat);
        }

        heartbeat.InstanceId = _instanceId;
        heartbeat.LastSeenAt = clock.UtcNow;
        heartbeat.State = "Running";
        heartbeat.DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            trackedUps = _trackers.Count,
            bootId = _bootId
        });
    }

    /// <summary>Aggiorna il file usato come health check dal container (§6.4).</summary>
    private void TouchHealthSentinel()
    {
        try
        {
            File.WriteAllText(_options.HealthSentinelPath,
                clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Impossibile aggiornare il file di health check {Path}.",
                _options.HealthSentinelPath);
        }
    }
}
