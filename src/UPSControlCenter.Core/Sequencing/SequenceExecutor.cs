using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Core.Sequencing;

/// <summary>
/// Esegue (o simula) una sequenza di step.
/// <para>
/// <b>È l'unico esecutore del sistema</b>: Orchestrator, Safety Agent e Simulatore lo
/// condividono, ciascuno fornendo i propri dispositivi, il proprio <see cref="IAdapterSelector"/>
/// (che in DRY_RUN restituisce sempre l'adapter simulato, §8.1) e il proprio
/// <see cref="ISequenceRunStore"/> per l'idempotenza. Il comportamento non può quindi divergere
/// fra un'esecuzione reale e una simulata: cambia solo cosa c'è dietro l'interfaccia.
/// </para>
/// <para>
/// L'istanza non ha stato mutabile: può essere registrata come singleton e usata da esecuzioni
/// concorrenti (Orchestrator e Simulatore potrebbero eseguire sequenze diverse nello stesso
/// istante) senza che una interferisca con l'altra.
/// </para>
/// </summary>
public sealed class SequenceExecutor(IAdapterSelector adapterSelector, IClock clock)
{
    /// <summary>
    /// Esegue tutti gli step, raggruppati per <see cref="ExecutableStep.ParallelGroup"/>.
    /// </summary>
    /// <param name="runId">Identificativo della run, usato come parte della chiave di idempotenza.</param>
    /// <param name="direction">Spegnimento o accensione: determina quale azione dell'adapter viene invocata.</param>
    /// <param name="steps">Step da eseguire, con i bersagli già risolti dal chiamante.</param>
    /// <param name="devices">Dispositivi coinvolti, indicizzati per id.</param>
    /// <param name="store">Persistenza dell'idempotenza (reale o in memoria).</param>
    /// <param name="evaluateCondition">
    /// Valuta la condizione di uno step (se presente). Se <c>null</c> o se la condizione non è
    /// valutabile (dato mancante), lo step viene eseguito: bloccare un'intera sequenza di
    /// spegnimento per una condizione accessoria non valutabile sarebbe più pericoloso che
    /// eseguirla (a differenza delle soglie di innesco, già gestite a monte da §9.3).
    /// </param>
    /// <param name="shouldStopAtBoundary">
    /// Controllato fra un gruppo di step e il successivo. Se vero, l'esecuzione si interrompe
    /// senza eseguire gli step restanti, senza che ciò sia considerato un errore (§11, caso B).
    /// </param>
    /// <param name="diagnostics">
    /// Canale diagnostico opzionale. Il Core non dipende da <c>Microsoft.Extensions.Logging</c>
    /// per restare senza dipendenze esterne (docs/03): i chiamanti che hanno un logger reale lo
    /// collegano qui.
    /// </param>
    /// <param name="delay">
    /// Funzione di attesa usata per <see cref="ExecutableStep.DelayBeforeSeconds"/>. Di default
    /// è <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, cioè tempo reale. Il Simulatore
    /// la sostituisce con una funzione che non attende: una sequenza di spegnimento può
    /// prevedere minuti di ritardo complessivi, e una simulazione deve restituire un esito in
    /// millisecondi, non riprodurre l'attesa reale (§14).
    /// </param>
    public async Task<SequenceExecutionResult> ExecuteAsync(
        string runId,
        SequenceDirection direction,
        IReadOnlyList<ExecutableStep> steps,
        IReadOnlyDictionary<Guid, ManagedDevice> devices,
        ISequenceRunStore store,
        Func<ConditionNode, ConditionResult>? evaluateCondition = null,
        Func<bool>? shouldStopAtBoundary = null,
        Action<EventSeverity, string>? diagnostics = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        delay ??= Task.Delay;

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(store);

        var actions = new List<StepActionOutcome>();
        var aborted = false;
        var anyUltimateFailure = false;
        var stoppedAtBoundary = false;
        string? stopReason = null;

        foreach (var group in GroupByExecutionOrder(steps))
        {
            if (shouldStopAtBoundary?.Invoke() == true)
            {
                stoppedAtBoundary = true;
                stopReason = "Segnale di interruzione al confine fra step (ritorno della corrente o kill switch).";
                diagnostics?.Invoke(EventSeverity.Warning, $"Sequenza {runId} interrotta al confine: {stopReason}");
                break;
            }

            ct.ThrowIfCancellationRequested();

            // Gli step dello stesso gruppo parallelo partono insieme; un gruppo singolo è il
            // caso normale (nessun ParallelGroup assegnato).
            var groupOutcomes = await Task.WhenAll(
                group.Select(step => ExecuteStepAsync(runId, direction, step, devices, store,
                    evaluateCondition, diagnostics, delay, ct)));

            foreach (var stepOutcomes in groupOutcomes)
                actions.AddRange(stepOutcomes.Outcomes);

            if (groupOutcomes.Any(g => g.AnyUltimateFailure))
                anyUltimateFailure = true;

            // Un solo step del gruppo che fallisce senza ContinueOnError interrompe l'intera
            // sequenza: non si procede con step successivi che potrebbero assumere una
            // precondizione non verificata (es. storage non ancora smontato).
            var hardFailure = groupOutcomes.Any(g => g.HardFailure);
            if (hardFailure)
            {
                aborted = true;
                stopReason = "Uno step è fallito senza ContinueOnError: sequenza interrotta.";
                diagnostics?.Invoke(EventSeverity.Critical, $"Sequenza {runId} interrotta per errore: {stopReason}");
                break;
            }
        }

        var finalState = stoppedAtBoundary
            ? RunState.Aborted
            : aborted || anyUltimateFailure
                ? RunState.Failed
                : RunState.Completed;

        return new SequenceExecutionResult
        {
            FinalState = finalState,
            Actions = actions,
            StoppedAtBoundary = stoppedAtBoundary,
            StopReason = stopReason
        };
    }

    /// <summary>
    /// Raggruppa gli step per esecuzione: quelli con lo stesso <see cref="ExecutableStep.ParallelGroup"/>
    /// non nullo formano un gruppo eseguito insieme; gli altri restano singoli. I gruppi sono
    /// ordinati per il più piccolo <c>OrderIndex</c> al loro interno, così l'ordine complessivo
    /// resta quello configurato anche quando gli indici dei membri non sono contigui.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<ExecutableStep>> GroupByExecutionOrder(
        IReadOnlyList<ExecutableStep> steps)
    {
        var ordered = steps.OrderBy(s => s.OrderIndex).ToList();
        var seen = new HashSet<Guid>();
        var result = new List<(int MinOrder, List<ExecutableStep> Items)>();

        foreach (var step in ordered)
        {
            if (!seen.Add(step.Id)) continue;

            if (step.ParallelGroup is { } group)
            {
                var members = ordered.Where(s => s.ParallelGroup == group).ToList();
                foreach (var m in members) seen.Add(m.Id);
                result.Add((members.Min(m => m.OrderIndex), members));
            }
            else
            {
                result.Add((step.OrderIndex, [step]));
            }
        }

        return result.OrderBy(r => r.MinOrder).Select(r => (IReadOnlyList<ExecutableStep>)r.Items);
    }

    private sealed record StepResult(IReadOnlyList<StepActionOutcome> Outcomes, bool HardFailure,
        bool AnyUltimateFailure);

    private async Task<StepResult> ExecuteStepAsync(
        string runId,
        SequenceDirection direction,
        ExecutableStep step,
        IReadOnlyDictionary<Guid, ManagedDevice> devices,
        ISequenceRunStore store,
        Func<ConditionNode, ConditionResult>? evaluateCondition,
        Action<EventSeverity, string>? diagnostics,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        if (step.DelayBeforeSeconds > 0)
            await delay(TimeSpan.FromSeconds(step.DelayBeforeSeconds), ct);

        // Una condizione falsa salta lo step per tutti i bersagli. Un esito Unknown non blocca:
        // la condizione sugli step è un affinamento, non la soglia di sicurezza primaria (§9.3
        // si applica alla decisione di avviare la sequenza, già presa a monte).
        if (step.Condition is not null && evaluateCondition is not null)
        {
            var result = evaluateCondition(step.Condition);
            if (result == ConditionResult.False)
            {
                var skipped = NoTargetsFor(step)
                    ? [MakeOutcome(step, null, 1, ActionState.Skipped, "CONDITION_FALSE",
                        "Condizione non soddisfatta: step saltato.", true)]
                    : step.TargetDeviceIds.Select(id => MakeOutcome(step, id, 1,
                        ActionState.Skipped, "CONDITION_FALSE", "Condizione non soddisfatta: step saltato.", true))
                        .ToList();

                return new StepResult(skipped, HardFailure: false, AnyUltimateFailure: false);
            }
        }

        if (step.StepType == SequenceStepType.Notify)
        {
            // La notifica non ha un bersaglio-dispositivo e non può "fallire" dal punto di vista
            // della sequenza: un canale di notifica guasto non deve bloccare lo spegnimento (§17).
            var outcome = MakeOutcome(step, null, 1, ActionState.Succeeded, "NOTIFIED",
                "Notifica registrata.", true);
            return new StepResult([outcome], HardFailure: false, AnyUltimateFailure: false);
        }

        if (step.StepType == SequenceStepType.ShutdownNutHost)
        {
            // Il sistema nasce e resta in DRY_RUN: questo step non esegue mai un arresto reale
            // del mini PC, in nessuna configurazione. È una registrazione, non un'azione (§13, §15).
            diagnostics?.Invoke(EventSeverity.Warning,
                $"Step «{step.Name}» di tipo ShutdownNutHost raggiunto: registrato come simulato. " +
                "Nessun comando di arresto del mini PC viene inviato in questa fase del progetto.");
            var outcome = MakeOutcome(step, null, 1, ActionState.Succeeded, "HOST_SHUTDOWN_NOOP",
                "Arresto dell'host NUT non eseguito: funzionalità non attivabile (DRY_RUN permanente per questo step).",
                true);
            return new StepResult([outcome], HardFailure: false, AnyUltimateFailure: false);
        }

        var targets = step.TargetDeviceIds;
        if (targets.Count == 0)
        {
            // Wait, VerifyState o Conditional senza bersaglio esplicito: nessuna azione da fare.
            var outcome = MakeOutcome(step, null, 1, ActionState.Succeeded, "NO_TARGET",
                "Nessun dispositivo bersaglio per questo step.", true);
            return new StepResult([outcome], HardFailure: false, AnyUltimateFailure: false);
        }

        var perDeviceResults = await Task.WhenAll(
            targets.Select(deviceId => ExecuteForDeviceAsync(runId, direction, step, deviceId,
                devices, store, diagnostics, ct)));

        // Ogni dispositivo può contribuire più di un tentativo (retry): si registrano tutti, ma
        // l'esito che conta è l'ultimo per dispositivo, perché un fallimento seguito da un retry
        // riuscito non è un fallimento. "AnyUltimateFailure" alimenta lo stato finale della
        // sequenza anche quando ContinueOnError ha permesso di procedere comunque.
        var outcomes = perDeviceResults.SelectMany(r => r).ToList();
        var lastPerDevice = perDeviceResults.Select(attempts => attempts[^1]).ToList();
        var anyUltimateFailure = lastPerDevice.Any(o => o.State is ActionState.Failed or ActionState.TimedOut);
        var anyHardFailure = anyUltimateFailure && !step.ContinueOnError;

        return new StepResult(outcomes, anyHardFailure, anyUltimateFailure);
    }

    private static bool NoTargetsFor(ExecutableStep step) => step.TargetDeviceIds.Count == 0;

    /// <summary>Esegue uno step per un dispositivo, con i retry. Restituisce tutti i tentativi, non solo l'ultimo.</summary>
    private async Task<IReadOnlyList<StepActionOutcome>> ExecuteForDeviceAsync(
        string runId,
        SequenceDirection direction,
        ExecutableStep step,
        Guid deviceId,
        IReadOnlyDictionary<Guid, ManagedDevice> devices,
        ISequenceRunStore store,
        Action<EventSeverity, string>? diagnostics,
        CancellationToken ct)
    {
        if (await store.IsStepAlreadyDoneAsync(runId, step.Id, deviceId, ct))
        {
            diagnostics?.Invoke(EventSeverity.Information,
                $"Step «{step.Name}» già completato per il dispositivo {deviceId}: salto idempotente.");
            return [MakeOutcome(step, deviceId, 1, ActionState.Skipped, "ALREADY_DONE",
                "Azione già completata in una esecuzione precedente.", true)];
        }

        if (!devices.TryGetValue(deviceId, out var device))
        {
            diagnostics?.Invoke(EventSeverity.Critical,
                $"Step «{step.Name}»: dispositivo {deviceId} non presente nell'elenco fornito.");
            return [MakeOutcome(step, deviceId, 1, ActionState.Failed, "DEVICE_NOT_FOUND",
                "Dispositivo non trovato nell'elenco fornito all'esecutore.", true)];
        }

        var attempt = 1;
        var maxAttempts = Math.Max(1, step.MaxRetries + 1);
        var attempts = new List<StepActionOutcome>();
        StepActionOutcome last;

        do
        {
            if (!await store.TryBeginActionAsync(runId, step.Id, deviceId, attempt,
                    step.StepType.ToString(), ct))
            {
                // Un altro processo (o un tentativo precedente in questo stesso processo dopo un
                // crash) ha già registrato questa azione logica: non si duplica (§4.5, R2).
                diagnostics?.Invoke(EventSeverity.Information,
                    $"Azione già prenotata per «{step.Name}»/{deviceId}/tentativo {attempt}: salto.");
                attempts.Add(MakeOutcome(step, deviceId, attempt, ActionState.Skipped,
                    "ALREADY_CLAIMED", "Azione già prenotata da un'altra esecuzione.", true));
                return attempts;
            }

            var startedAt = clock.UtcNow;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

            try
            {
                last = await PerformActionAsync(step, device, direction, attempt, startedAt,
                    timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = MakeOutcome(step, deviceId, attempt, ActionState.TimedOut, "TIMEOUT",
                    $"Timeout dopo {step.TimeoutSeconds}s.", true, startedAt);
            }

            await store.CompleteActionAsync(runId, step.Id, deviceId, attempt, last.State,
                last.ResultCode, last.Message, last.WasDryRun, ct);

            attempts.Add(last);

            if (last.State == ActionState.Succeeded) break;

            attempt++;
        }
        while (attempt <= maxAttempts && !ct.IsCancellationRequested);

        return attempts;
    }

    /// <summary>
    /// Esegue l'azione tramite adapter e, se richiesto, verifica il risultato.
    /// <para>
    /// Un dispositivo non raggiungibile durante la verifica non viene mai interpretato come
    /// prova di arresto o di accensione: l'azione resta "riuscita" solo se l'adapter l'ha
    /// confermata, e la verifica aggiunge un'informazione, non la sostituisce (§10).
    /// </para>
    /// </summary>
    private async Task<StepActionOutcome> PerformActionAsync(ExecutableStep step,
        ManagedDevice device, SequenceDirection direction, int attempt, DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var adapter = adapterSelector.Select(device);

        AdapterResult result = step.StepType switch
        {
            SequenceStepType.ShutdownDevice or SequenceStepType.ShutdownGroup
                => await adapter.ShutdownAsync(device, ct),
            SequenceStepType.StartupDevice or SequenceStepType.StartupGroup
                => await adapter.StartupAsync(device, ct),
            SequenceStepType.VerifyState
                => await adapter.CheckStateAsync(device, ct),
            SequenceStepType.Wait
                => AdapterResult.Ok("WAIT", adapter.PerformsRealOperations is false,
                    "Attesa completata."),
            SequenceStepType.Conditional
                => AdapterResult.Ok("CONDITIONAL_PASSED", adapter.PerformsRealOperations is false,
                    "Condizione valutata: nessuna azione associata."),
            _ => AdapterResult.Fail("UNSUPPORTED_STEP_TYPE", true,
                $"Tipo di step non gestito dall'esecutore: {step.StepType}.")
        };

        if (!result.Success)
        {
            return MakeOutcome(step, device.Id, attempt, ActionState.Failed, result.ResultCode,
                result.Message, result.WasDryRun, startedAt);
        }

        if (!step.VerifyCompletion || step.StepType is not (SequenceStepType.ShutdownDevice
            or SequenceStepType.ShutdownGroup or SequenceStepType.StartupDevice
            or SequenceStepType.StartupGroup))
        {
            return MakeOutcome(step, device.Id, attempt, ActionState.Succeeded, result.ResultCode,
                result.Message, result.WasDryRun, startedAt);
        }

        var check = await adapter.CheckStateAsync(device, ct);
        var expected = direction == SequenceDirection.Shutdown ? DeviceState.Off : DeviceState.On;

        // Contraddizione netta (es. il dispositivo risulta ancora acceso dopo lo spegnimento):
        // l'azione viene considerata fallita e può essere ritentata. Uno stato inconclusivo
        // (Unknown/Unreachable) non contraddice l'azione riuscita: un ping senza risposta non è
        // prova di arresto, ma nemmeno prova del contrario (§10).
        if (check.ObservedState is { } observed && observed != expected
            && observed is DeviceState.On or DeviceState.Off)
        {
            return MakeOutcome(step, device.Id, attempt, ActionState.Failed, "VERIFICATION_MISMATCH",
                $"Verifica non superata: stato osservato {observed}, atteso {expected}.",
                result.WasDryRun, startedAt);
        }

        var message = check.ObservedState is DeviceState.Unreachable or DeviceState.Unknown or null
            ? $"{result.Message} Verifica non conclusiva: stato non osservabile."
            : result.Message;

        return MakeOutcome(step, device.Id, attempt, ActionState.Succeeded, result.ResultCode,
            message, result.WasDryRun, startedAt);
    }

    private StepActionOutcome MakeOutcome(ExecutableStep step, Guid? deviceId, int attempt,
        ActionState state, string? resultCode, string? message, bool wasDryRun,
        DateTimeOffset? startedAt = null) => new()
    {
        StepId = step.Id,
        DeviceId = deviceId,
        Attempt = attempt,
        State = state,
        ResultCode = resultCode,
        Message = message,
        WasDryRun = wasDryRun,
        StartedAt = startedAt ?? clock.UtcNow,
        FinishedAt = clock.UtcNow
    };
}
