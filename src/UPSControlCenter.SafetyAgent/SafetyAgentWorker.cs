using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Infrastructure.Sqlite;

namespace UPSControlCenter.SafetyAgent;

/// <summary>Stati del Safety Agent (§7.2).</summary>
public enum AgentState
{
    Initializing,
    /// <summary>Configurazione assente o non fidata: osserva e registra, non agisce.</summary>
    SafeHold,
    Reconciling,
    Monitoring,
    BlackoutOpen,
    Executing,
    PostShutdown
}

/// <summary>
/// Worker del Safety Agent (§13).
/// <para>
/// Garantisce la protezione anche con Docker, PostgreSQL e rete aziendale indisponibili: le
/// uniche dipendenze sono <c>upsd</c> su loopback e il database SQLite locale.
/// </para>
/// </summary>
public sealed class SafetyAgentWorker(
    SafetyStore store,
    INutClient nutClient,
    IClock clock,
    ConfigurationSynchronizer synchronizer,
    IHttpClientFactory httpClientFactory,
    IOptions<SafetyAgentOptions> options,
    ILogger<SafetyAgentWorker> logger) : BackgroundService
{
    private readonly SafetyAgentOptions _options = options.Value;
    private readonly RuleEvaluator _evaluator = new();
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
    private readonly string _bootId = BootIdProvider.Get();

    private AgentState _state = AgentState.Initializing;
    private SafetyConfiguration? _config;
    private BlackoutTracker? _tracker;
    private UpsSnapshot? _lastGoodSnapshot;
    private DateTimeOffset _lastConfigPull = DateTimeOffset.MinValue;
    private long _fencingToken;
    private string? _activeRunId;

    public AgentState State => _state;
    public SafetyConfiguration? ActiveConfiguration => _config;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Safety Agent avviato. Istanza {Instance}, boot {BootId}, database {Db}.",
            _instanceId, _bootId, _options.DatabasePath);

        store.EnsureSchema();
        await InitializeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Catch ampio deliberato: il ciclo di sicurezza non deve morire per un errore
                // transitorio. L'alternativa — terminare il processo — lascerebbe l'infrastruttura
                // senza protezione.
                logger.LogError(ex, "Errore non gestito nel ciclo del Safety Agent. Si prosegue.");
                store.RecordCriticalEvent(UpsEventType.DeviceError, EventSeverity.Critical, null,
                    $"Errore nel ciclo dell'agent: {ex.Message}", clock.UtcNow);
            }

            await Task.Delay(TimeSpan.FromSeconds(PollingInterval), stoppingToken);
        }

        logger.LogInformation("Safety Agent in arresto. Stato finale: {State}.", _state);
    }

    private int PollingInterval =>
        _config?.Ups.PollingIntervalSeconds ?? _options.FallbackPollingIntervalSeconds;

    /// <summary>
    /// Avvio: carica la configurazione locale e riconcilia lo stato precedente. Si parte dal
    /// database locale e non dalla rete, perché l'agent deve poter funzionare isolato (§7.2).
    /// </summary>
    private async Task InitializeAsync(CancellationToken ct)
    {
        _config = synchronizer.LoadActiveConfiguration();

        if (_config is null)
        {
            // Nessuna configurazione locale: si tenta una volta la rete, poi SafeHold.
            _config = await synchronizer.TrySyncAsync(ct);
            _lastConfigPull = clock.UtcNow;
        }

        if (_config is null)
        {
            _state = AgentState.SafeHold;
            logger.LogWarning(
                "Nessuna configurazione di sicurezza valida disponibile: stato SafeHold. " +
                "Il monitoraggio prosegue, nessuna azione verrà eseguita.");
            return;
        }

        _fencingToken = store.GetCoordination().FencingToken;
        _state = AgentState.Reconciling;
        Reconcile();
    }

    /// <summary>
    /// Riconcilia blackout e sequenze aperti dopo un riavvio dell'agent o del sistema (§7.5).
    /// </summary>
    private void Reconcile()
    {
        var (lastBootId, _) = store.GetRecoveryState();
        var rebooted = lastBootId is not null && lastBootId != _bootId;

        var open = store.GetOpenBlackout();

        if (open is null)
        {
            _state = AgentState.Monitoring;
            _tracker = CreateTracker(null);
            store.UpdateRecoveryState(_bootId, clock.UtcNow, pendingReconciliation: false);
            logger.LogInformation("Riconciliazione completata: nessun blackout aperto.");
            return;
        }

        var reconciled = BlackoutTracker.Reconcile(open, _bootId, clock.UtcNow,
            RuleEvaluator.MaxPlausibleBlackoutDuration);

        if (!reconciled.DurationCertain)
        {
            logger.LogWarning(
                "Blackout {BlackoutId} ripreso dopo un riavvio ({Rebooted}) ma la durata non è " +
                "ricostruibile in modo affidabile. Si applica la policy {Policy}.",
                reconciled.BlackoutId, rebooted, _config!.Thresholds.UncertainDurationPolicy);

            store.RecordCriticalEvent(UpsEventType.BlackoutDurationUncertain, EventSeverity.Warning,
                reconciled.BlackoutId,
                $"Durata non ricostruibile. Boot precedente: {lastBootId ?? "sconosciuto"}, " +
                $"attuale: {_bootId}. Policy applicata: {_config.Thresholds.UncertainDurationPolicy}.",
                clock.UtcNow);
        }

        store.UpsertBlackout(reconciled, "RECONCILED", null, null);
        _tracker = CreateTracker(reconciled);
        _state = AgentState.BlackoutOpen;
        store.UpdateRecoveryState(_bootId, clock.UtcNow, pendingReconciliation: false);

        logger.LogInformation(
            "Blackout {BlackoutId} ripreso dallo stato persistito (durata certa: {Certain}).",
            reconciled.BlackoutId, reconciled.DurationCertain);
    }

    private BlackoutTracker CreateTracker(BlackoutState? restored) =>
        new(_config!.Ups.NutUpsName, clock, _bootId,
            _config.Thresholds.DebounceSamples, restored);

    private async Task RunCycleAsync(CancellationToken ct)
    {
        // 1. Kill switch: controllato per primo, prima di qualunque valutazione (§8.3).
        var automationDisabled = IsAutomationDisabled();

        // 2. Sincronizzazione configurazione, a intervalli, senza bloccare il monitoraggio.
        if (clock.UtcNow - _lastConfigPull >= TimeSpan.FromSeconds(_options.ConfigPullIntervalSeconds))
        {
            _lastConfigPull = clock.UtcNow;
            var synced = await synchronizer.TrySyncAsync(ct);

            if (synced is not null && _config is null)
            {
                // Si esce da SafeHold solo quando arriva una configurazione fidata.
                _config = synced;
                _state = AgentState.Reconciling;
                Reconcile();
            }
            else if (synced is not null && synced.Version != _config?.Version)
            {
                logger.LogInformation(
                    "Nuova configurazione attiva: versione {Old} → {New}.",
                    _config?.Version, synced.Version);
                _config = synced;
                _tracker = CreateTracker(_tracker?.Current);
            }
        }

        if (_config is null)
        {
            // SafeHold: si continua a leggere NUT per avere visibilità, ma non si decide nulla.
            await ObserveOnlyAsync(ct);
            return;
        }

        // 3. Lettura NUT.
        var snapshot = await ReadUpsAsync(ct);
        var outcome = _tracker!.Process(snapshot);

        if (snapshot.IsUsableForDecisions) _lastGoodSnapshot = snapshot;

        // 4. Persistenza dello stato del blackout, così un crash non perde il riferimento temporale.
        if (outcome.Blackout is { } blackout)
        {
            store.UpsertBlackout(blackout, outcome.DebouncedStatus.ToNutString(),
                snapshot.BatteryRuntimeSeconds, snapshot.BatteryChargePercent);
        }

        foreach (var evt in outcome.Events)
        {
            var severity = evt switch
            {
                UpsEventType.BlackoutStarted or UpsEventType.LowBattery
                    or UpsEventType.BatteryCritical => EventSeverity.Critical,
                UpsEventType.PowerRestored or UpsEventType.CommunicationRestored => EventSeverity.Information,
                _ => EventSeverity.Warning
            };
            store.RecordCriticalEvent(evt, severity, outcome.Blackout?.BlackoutId,
                snapshot.StatusRaw, clock.UtcNow);
        }

        // 5. Valutazione con il motore condiviso.
        var coordination = store.GetCoordination();
        var ctx = new EvaluationContext
        {
            Snapshot = snapshot,
            Policy = ToPolicy(_config),
            Blackout = outcome.Blackout,
            DebouncedStatus = outcome.DebouncedStatus,
            NowUtc = clock.UtcNow,
            NowMonotonicMs = clock.MonotonicMilliseconds,
            BootId = _bootId,
            OrchestratorHeartbeatAge = coordination.OrchestratorLastSeen is { } seen
                ? clock.UtcNow - seen
                : null,
            AutomationDisabled = automationDisabled
        };

        var decision = _evaluator.Evaluate(ctx, []);

        UpdateState(outcome, decision);

        // 6. Esecuzione, solo se l'agent è il proprietario legittimo.
        if (decision.ShouldTriggerShutdown && _activeRunId is null)
            await TryExecuteAsync(ctx, decision, coordination, ct);

        // 7. Heartbeat verso systemd e, se raggiungibile, verso il Control Plane.
        store.UpdateRecoveryState(_bootId, clock.UtcNow, pendingReconciliation: false);
        await ReportHeartbeatAsync(ctx, decision, ct);
    }

    /// <summary>Stato SafeHold: lettura e registrazione, nessuna decisione.</summary>
    private async Task ObserveOnlyAsync(CancellationToken ct)
    {
        _state = AgentState.SafeHold;
        var snapshot = await ReadUpsAsync(ct);

        logger.LogInformation(
            "[SafeHold] Stato UPS {Status}, autonomia {Runtime}. Nessuna configurazione fidata: " +
            "nessuna azione possibile.",
            snapshot.Status.ToNutString(),
            snapshot.BatteryRuntimeSeconds is { } r ? $"{r}s" : "non disponibile");
    }

    private async Task<UpsSnapshot> ReadUpsAsync(CancellationToken ct)
    {
        var upsName = _config?.Ups.NutUpsName ?? "riello";
        var snapshot = await nutClient.ReadAsync(upsName, ct);

        // Freschezza valutata qui e non nel client: la soglia è una decisione di policy.
        var threshold = _config?.Ups.StaleDataThresholdSeconds ?? 60;
        if (snapshot.CommunicationOk
            && clock.UtcNow - snapshot.AcquiredAt > TimeSpan.FromSeconds(threshold))
        {
            return snapshot with { IsStale = true };
        }

        return snapshot;
    }

    private void UpdateState(TrackerOutcome outcome, RuleDecision decision)
    {
        if (_activeRunId is not null)
        {
            _state = AgentState.Executing;
            return;
        }

        _state = outcome.Blackout switch
        {
            { IsOpen: true } => AgentState.BlackoutOpen,
            { IsOpen: false } => AgentState.PostShutdown,
            _ => AgentState.Monitoring
        };

        if (decision.CriticalConditions.Count > 0)
        {
            logger.LogWarning("Condizioni critiche rilevate: {Conditions}. {Reason}",
                string.Join(", ", decision.CriticalConditions), decision.Reason);
        }
    }

    /// <summary>
    /// Avvia la sequenza solo se l'orchestrator è assente da oltre la grazia configurata e se il
    /// vincolo di unicità lo consente: Orchestrator e Safety Agent non possono mai eseguire la
    /// stessa azione (§13).
    /// </summary>
    private async Task TryExecuteAsync(EvaluationContext ctx, RuleDecision decision,
        CoordinationState coordination, CancellationToken ct)
    {
        if (ctx.Blackout is not { } blackout) return;

        var grace = TimeSpan.FromSeconds(_config!.TakeoverGraceSeconds);
        var orchestratorAlive = coordination.OrchestratorLastSeen is { } seen
                                && clock.UtcNow - seen <= grace;

        if (orchestratorAlive)
        {
            logger.LogInformation(
                "Soglia raggiunta ({Reason}) ma l'orchestrator è attivo (ultimo heartbeat " +
                "{Age:F0}s): l'esecuzione resta a lui.",
                decision.Reason, (clock.UtcNow - coordination.OrchestratorLastSeen!.Value).TotalSeconds);
            return;
        }

        var reason = coordination.OrchestratorLastSeen is null
            ? "Nessun heartbeat dell'orchestrator registrato."
            : $"Heartbeat dell'orchestrator più vecchio di {grace.TotalSeconds:F0}s.";

        _fencingToken = store.PerformTakeover(nameof(WorkerRole.SafetyAgent), reason, clock.UtcNow);

        store.RecordCriticalEvent(UpsEventType.TakeoverPerformed, EventSeverity.Critical,
            blackout.BlackoutId, reason, clock.UtcNow);

        var runId = $"{blackout.BlackoutId}:shutdown";
        var sequenceId = _config.ShutdownSteps.Count > 0 ? "configured" : "empty";

        if (!store.TryBeginSequenceRun(runId, blackout.BlackoutId, sequenceId, _config.Version,
                _fencingToken, _config.ExecutionMode, WorkerRole.SafetyAgent, decision.Reason,
                clock.UtcNow))
        {
            // Il vincolo ha impedito la duplicazione: un'altra esecuzione è già in corso.
            return;
        }

        _activeRunId = runId;
        _state = AgentState.Executing;

        logger.LogWarning(
            "SEQUENZA AVVIATA dal Safety Agent in modalità {Mode}. Blackout {BlackoutId}. " +
            "Motivo: {Reason}",
            _config.ExecutionMode, blackout.BlackoutId, decision.Reason);

        await ExecuteStepsAsync(runId, ct);
    }

    /// <summary>
    /// Esegue gli step. In Fase 1 ogni azione è registrata come simulata: l'esecutore completo
    /// con adapter, dipendenze e verifiche appartiene alle Fasi 2-3. La registrazione idempotente
    /// e il percorso di coordinamento sono però già quelli definitivi.
    /// </summary>
    private async Task ExecuteStepsAsync(string runId, CancellationToken ct)
    {
        var steps = _config!.ShutdownSteps.OrderBy(s => s.OrderIndex).ToList();

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();

            if (step.DelayBeforeSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(step.DelayBeforeSeconds), ct);

            IReadOnlyList<Guid?> targets = step.TargetDeviceIds.Count > 0
                ? [.. step.TargetDeviceIds.Cast<Guid?>()]
                : [step.TargetDeviceId];

            foreach (var deviceId in targets)
            {
                var opId = $"{runId}:{step.Id}:{deviceId?.ToString() ?? "-"}:1";

                // In DRY_RUN non viene invocato nessun adapter reale: non ne esistono di
                // registrati e l'esito è una registrazione, non un comando (§8.1).
                var recorded = store.TryRecordOperation(opId, runId, step.Id.ToString(),
                    deviceId?.ToString(), attempt: 1, step.StepType.ToString(),
                    ActionState.Succeeded, clock.UtcNow, clock.UtcNow,
                    _config.IsDryRun ? "DRY_RUN" : "EXECUTED",
                    _config.IsDryRun
                        ? $"[DRY_RUN] Step «{step.Name}» registrato senza invio di comandi."
                        : $"Step «{step.Name}» eseguito.",
                    wasDryRun: _config.IsDryRun);

                if (!recorded)
                    logger.LogInformation(
                        "Step «{Step}» già eseguito per questo blackout: salto idempotente.",
                        step.Name);
            }
        }

        store.CompleteSequenceRun(runId, RunState.Completed, clock.UtcNow);
        _activeRunId = null;
        _state = AgentState.PostShutdown;

        store.RecordCriticalEvent(UpsEventType.SequenceCompleted, EventSeverity.Information,
            null, $"Sequenza {runId} completata ({steps.Count} step).", clock.UtcNow);

        logger.LogWarning("Sequenza {RunId} completata: {Count} step registrati in modalità {Mode}.",
            runId, steps.Count, _config.ExecutionMode);
    }

    private bool IsAutomationDisabled()
    {
        try
        {
            return File.Exists(_options.KillSwitchPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nel dubbio si considerano disabilitate: è la scelta sicura (§8.3).
            logger.LogError(ex,
                "Impossibile leggere il file sentinella {Path}: automazioni considerate disabilitate.",
                _options.KillSwitchPath);
            return true;
        }
    }

    /// <summary>
    /// Invia l'heartbeat al Control Plane. È best-effort: un fallimento non ha conseguenze sul
    /// comportamento dell'agent, che non dipende dal portale (§13).
    /// </summary>
    private async Task ReportHeartbeatAsync(EvaluationContext ctx, RuleDecision decision,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ControlPlaneBaseUrl)) return;

        try
        {
            var client = httpClientFactory.CreateClient("ControlPlane");
            client.BaseAddress = new Uri(_options.ControlPlaneBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);

            if (!string.IsNullOrWhiteSpace(_options.AgentToken))
                client.DefaultRequestHeaders.Add("X-Agent-Token", _options.AgentToken);

            await client.PostAsJsonAsync("api/safety/heartbeat", new AgentHeartbeatRequest
            {
                InstanceId = _instanceId,
                State = _state.ToString(),
                ActiveConfigVersion = _config?.Version ?? 0,
                FencingToken = _fencingToken,
                CurrentBlackoutId = ctx.Blackout?.BlackoutId,
                BlackoutDurationSeconds = ctx.BlackoutDuration is { } d ? (int)d.TotalSeconds : null,
                AutomationDisabled = ctx.AutomationDisabled,
                LastUpsStatus = ctx.DebouncedStatus.ToNutString()
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or UriFormatException)
        {
            logger.LogDebug("Heartbeat verso il Control Plane non inviato: {Error}.", ex.Message);
        }
    }

    /// <summary>
    /// Adatta la configurazione di sicurezza al tipo atteso dal motore. L'alternativa — un motore
    /// che accetti due tipi diversi — introdurrebbe il rischio di comportamenti divergenti fra
    /// Orchestrator e Safety Agent, che è esattamente ciò che l'architettura evita.
    /// </summary>
    internal static ShutdownPolicy ToPolicy(SafetyConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ShutdownPolicy
        {
            Name = $"safety-config-v{config.Version}",
            IsActive = true,
            ExecutionMode = config.ExecutionMode,
            MaxBlackoutDurationSeconds = config.Thresholds.MaxBlackoutDurationSeconds,
            MinRemainingRuntimeSeconds = config.Thresholds.MinRemainingRuntimeSeconds,
            DebounceSamples = config.Thresholds.DebounceSamples,
            CriticalBatteryChargePercent = config.Thresholds.CriticalBatteryChargePercent,
            UnknownRuntimePolicy = config.Thresholds.UnknownRuntimePolicy,
            UncertainDurationPolicy = config.Thresholds.UncertainDurationPolicy,
            CommunicationLostPolicy = config.Thresholds.CommunicationLostPolicy,
            CommunicationLostGraceSeconds = config.Thresholds.CommunicationLostGraceSeconds,
            TakeoverGraceSeconds = config.TakeoverGraceSeconds
        };
    }
}
