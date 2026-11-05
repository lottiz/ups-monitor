using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.SafetyAgent;

/// <summary>
/// Converte i modelli leggeri di <see cref="SafetyConfiguration"/> (pensati per viaggiare
/// firmati dal Control Plane, §3.3) nei modelli richiesti da <see cref="SequenceExecutor"/>.
/// <para>
/// Il Safety Agent non ha accesso a PostgreSQL e quindi non può caricare le entità EF Core
/// complete: i <see cref="ManagedDevice"/> prodotti qui sono oggetti transitori, mai persistiti,
/// che esistono solo per fornire agli adapter i campi di cui hanno bisogno.
/// </para>
/// </summary>
internal static class SafetyExecutionMapper
{
    public static ExecutableStep ToExecutableStep(SafetyStep step) => new()
    {
        Id = step.Id,
        OrderIndex = step.OrderIndex,
        Name = step.Name,
        StepType = step.StepType,
        TargetDeviceIds = step.TargetDeviceId is { } single
            ? [single, .. step.TargetDeviceIds.Where(id => id != single)]
            : step.TargetDeviceIds,
        ParallelGroup = step.ParallelGroup,
        DelayBeforeSeconds = step.DelayBeforeSeconds,
        TimeoutSeconds = step.TimeoutSeconds,
        MaxRetries = step.MaxRetries,
        ContinueOnError = step.ContinueOnError,
        VerifyCompletion = step.VerifyCompletion
        // Nessuna condizione per step: la configurazione di sicurezza non la trasporta ancora.
        // Le condizioni sulle regole di innesco sono già valutate a monte dal RuleEvaluator.
    };

    public static ManagedDevice ToManagedDevice(SafetyDevice device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Hostname = device.Hostname,
        DeviceType = device.DeviceType,
        ShutdownProtocol = device.ShutdownProtocol,
        Priority = device.Priority,
        ShutdownTimeoutSeconds = device.ShutdownTimeoutSeconds,
        PostActionDelaySeconds = device.PostActionDelaySeconds,
        StatusCheckMethod = device.StatusCheckMethod,
        IsEnabled = device.IsEnabled
    };
}
