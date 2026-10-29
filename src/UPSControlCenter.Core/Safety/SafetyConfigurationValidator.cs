using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.Core.Safety;

public sealed record ValidationResult
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static ValidationResult Success(IReadOnlyList<string>? warnings = null) =>
        new() { IsValid = true, Warnings = warnings ?? [] };
}

/// <summary>
/// Valida una configurazione prima della pubblicazione (§3.3). È l'ultima barriera prima che una
/// configurazione raggiunga il Safety Agent: un errore qui diventa un comportamento errato durante
/// un blackout reale, quindi la validazione è volutamente severa.
/// </summary>
public sealed class SafetyConfigurationValidator
{
    public ValidationResult Validate(SafetyConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateVersionAndMode(config, errors, warnings);
        ValidateUps(config.Ups, errors);
        ValidateThresholds(config.Thresholds, errors, warnings);
        ValidateDevices(config.Devices, errors);
        ValidateSteps(config, errors, warnings);
        ValidateDependencies(config, errors);

        return errors.Count == 0
            ? ValidationResult.Success(warnings)
            : new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
    }

    private static void ValidateVersionAndMode(SafetyConfiguration config, List<string> errors,
        List<string> warnings)
    {
        if (config.Version <= 0)
            errors.Add("La versione della configurazione deve essere un intero positivo.");

        // La modalità LIVE non è un errore, ma richiede che gli adapter siano stati abilitati
        // singolarmente: una configurazione LIVE senza adapter non farebbe nulla ed è un sintomo
        // di configurazione incompleta.
        if (config.ExecutionMode == ExecutionMode.Live)
        {
            if (config.LiveEnabledAdapters.Count == 0)
                errors.Add("Modalità LIVE senza alcun adapter abilitato: configurazione incoerente. " +
                           "Abilitare esplicitamente gli adapter richiesti (§8.2).");
            else
                warnings.Add($"Configurazione in modalità LIVE con adapter abilitati: " +
                             $"{string.Join(", ", config.LiveEnabledAdapters)}. Verificare l'approvazione.");
        }
        else if (config.LiveEnabledAdapters.Count > 0)
        {
            errors.Add("Modalità DRY_RUN con adapter LIVE abilitati: combinazione non ammessa.");
        }
    }

    private static void ValidateUps(SafetyUpsConfig ups, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(ups.NutHost))
            errors.Add("Host NUT non specificato.");
        if (ups.NutPort is < 1 or > 65535)
            errors.Add($"Porta NUT non valida: {ups.NutPort}.");
        if (string.IsNullOrWhiteSpace(ups.NutUpsName))
            errors.Add("Nome UPS NUT non specificato.");
        if (ups.PollingIntervalSeconds is < 2 or > 300)
            errors.Add($"Intervallo di polling fuori intervallo (2-300s): {ups.PollingIntervalSeconds}.");
        if (ups.StaleDataThresholdSeconds <= ups.PollingIntervalSeconds)
            errors.Add("La soglia di obsolescenza deve essere maggiore dell'intervallo di polling, " +
                       "altrimenti ogni lettura risulterebbe obsoleta.");
    }

    private static void ValidateThresholds(SafetyThresholds t, List<string> errors, List<string> warnings)
    {
        if (t.MaxBlackoutDurationSeconds is < 30 or > 86400)
            errors.Add($"Durata massima blackout fuori intervallo (30-86400s): {t.MaxBlackoutDurationSeconds}.");

        if (t.MinRemainingRuntimeSeconds is < 60 or > 86400)
            errors.Add($"Autonomia minima residua fuori intervallo (60-86400s): {t.MinRemainingRuntimeSeconds}.");

        if (t.DebounceSamples is < 1 or > 60)
            errors.Add($"Numero di campioni di debounce fuori intervallo (1-60): {t.DebounceSamples}.");

        if (t.CriticalBatteryChargePercent is { } c && c is < 0 or > 100)
            errors.Add($"Soglia di carica critica non valida: {c}%.");

        // Una soglia di autonomia molto bassa lascia poco margine alla sequenza di spegnimento.
        if (t.MinRemainingRuntimeSeconds < 300)
            warnings.Add($"Autonomia minima di soli {t.MinRemainingRuntimeSeconds}s: verificare che " +
                         "sia sufficiente a completare l'intera sequenza di spegnimento.");

        if (t.UnknownRuntimePolicy == UnknownRuntimePolicy.IgnoreRuntimeCondition)
            warnings.Add("Policy IgnoreRuntimeCondition: se l'UPS non fornisce l'autonomia, la " +
                         "protezione si basa solo sulla durata del blackout e sullo stato LB.");
    }

    private static void ValidateDevices(IReadOnlyList<SafetyDevice> devices, List<string> errors)
    {
        var seen = new HashSet<Guid>();
        foreach (var d in devices)
        {
            if (!seen.Add(d.Id))
                errors.Add($"Dispositivo duplicato nella configurazione: {d.Id}.");

            if (string.IsNullOrWhiteSpace(d.Name))
                errors.Add($"Dispositivo {d.Id} senza nome.");

            if (d.ShutdownTimeoutSeconds is < 10 or > 3600)
                errors.Add($"Timeout di spegnimento non valido per «{d.Name}»: {d.ShutdownTimeoutSeconds}s.");

            // Un dispositivo abilitato senza protocollo non è spegnibile: va dichiarato, non ignorato.
            if (d.IsEnabled && d.ShutdownProtocol == ShutdownProtocol.None)
                errors.Add($"Dispositivo «{d.Name}» abilitato ma senza protocollo di spegnimento.");
        }
    }

    private static void ValidateSteps(SafetyConfiguration config, List<string> errors,
        List<string> warnings)
    {
        var deviceIds = config.Devices.Select(d => d.Id).ToHashSet();
        var orders = new HashSet<int>();

        foreach (var step in config.ShutdownSteps)
        {
            if (!orders.Add(step.OrderIndex))
                errors.Add($"Indice di ordinamento duplicato nella sequenza: {step.OrderIndex}.");

            if (step.TimeoutSeconds is < 5 or > 7200)
                errors.Add($"Timeout non valido per lo step «{step.Name}»: {step.TimeoutSeconds}s.");

            if (step.MaxRetries is < 0 or > 5)
                errors.Add($"Numero di tentativi non valido per lo step «{step.Name}»: {step.MaxRetries}.");

            IReadOnlyList<Guid> targets = step.TargetDeviceId is { } single
                ? [single]
                : step.TargetDeviceIds;
            foreach (var t in targets)
            {
                if (!deviceIds.Contains(t))
                    errors.Add($"Lo step «{step.Name}» fa riferimento a un dispositivo inesistente: {t}.");
            }

            if (step.StepType is SequenceStepType.ShutdownDevice or SequenceStepType.ShutdownGroup
                && targets.Count == 0)
                errors.Add($"Lo step di spegnimento «{step.Name}» non ha dispositivi bersaglio.");

            // Lo spegnimento del mini PC NUT termina la capacità di controllo: ammesso solo per ultimo.
            if (step.StepType == SequenceStepType.ShutdownNutHost)
            {
                var isLast = config.ShutdownSteps.All(s => s.OrderIndex <= step.OrderIndex);
                if (!isLast)
                    errors.Add($"Lo step «{step.Name}» arresta l'host NUT ma non è l'ultimo della " +
                               "sequenza: dopo di esso nessuna azione sarebbe eseguibile (§13).");
                else
                    warnings.Add($"La sequenza prevede l'arresto dell'host NUT («{step.Name}»): " +
                                 "verificare la strategia di riaccensione autonoma del mini PC (§12).");
            }

            if (!step.VerifyCompletion && step.StepType == SequenceStepType.ShutdownDevice)
                warnings.Add($"Lo step «{step.Name}» non verifica il completamento: la sequenza " +
                             "procederebbe senza prova dell'avvenuto arresto (§10).");
        }
    }

    private static void ValidateDependencies(SafetyConfiguration config, List<string> errors)
    {
        var deviceIds = config.Devices.Select(d => d.Id).ToHashSet();

        foreach (var dep in config.Dependencies)
        {
            if (dep.DependentDeviceId == dep.DependsOnDeviceId)
            {
                errors.Add($"Dipendenza di un dispositivo da se stesso: {dep.DependentDeviceId}.");
                continue;
            }
            if (!deviceIds.Contains(dep.DependentDeviceId))
                errors.Add($"Dipendenza con dispositivo dipendente inesistente: {dep.DependentDeviceId}.");
            if (!deviceIds.Contains(dep.DependsOnDeviceId))
                errors.Add($"Dipendenza verso dispositivo inesistente: {dep.DependsOnDeviceId}.");
        }

        // Il grafo deve essere aciclico: un ciclo rende l'ordine di spegnimento indefinito (§10).
        var asEntities = config.Dependencies.Select(d => new DeviceDependency
        {
            DependentDeviceId = d.DependentDeviceId,
            DependsOnDeviceId = d.DependsOnDeviceId,
            DependencyType = d.DependencyType,
            IsHard = d.IsHard
        }).ToList();

        var graph = new DependencyGraph(deviceIds, asEntities);
        var validation = graph.Validate();
        if (!validation.IsValid)
            errors.AddRange(validation.Errors);
    }
}
