using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Adapters;

/// <summary>Configurazione della modalità di esecuzione effettiva.</summary>
public sealed class ExecutionModeOptions
{
    public const string SectionName = "Execution";

    /// <summary>Modalità richiesta dalla configurazione. Il valore predefinito è DRY_RUN.</summary>
    public ExecutionMode Mode { get; set; } = ExecutionMode.DryRun;

    /// <summary>Adapter abilitati a operazioni reali, ciascuno con approvazione separata (§8.2).</summary>
    public IList<string> LiveEnabledAdapters { get; set; } = [];

    /// <summary>
    /// Autorizzazione a livello di deployment, da variabile d'ambiente <c>UPS_ALLOW_LIVE</c>.
    /// Senza di essa la modalità LIVE presente nel database viene ignorata: evita che il
    /// ripristino di un dump proveniente da un altro ambiente attivi operazioni reali (§8.2, R18).
    /// </summary>
    public bool DeploymentAllowsLive { get; set; }

    /// <summary>
    /// Modalità effettiva: LIVE solo se richiesta <b>e</b> autorizzata dal deployment.
    /// Ogni altra combinazione ricade su DRY_RUN.
    /// </summary>
    public ExecutionMode EffectiveMode =>
        Mode == ExecutionMode.Live && DeploymentAllowsLive
            ? ExecutionMode.Live
            : ExecutionMode.DryRun;
}

/// <summary>
/// Registrazione degli adapter. È qui che vive la garanzia di DRY_RUN: in assenza di modalità
/// LIVE effettiva <b>nessun adapter reale viene registrato nel contenitore</b>, quindi non può
/// essere risolto né eseguito (§8.1).
/// </summary>
public static class AdapterRegistration
{
    public static IServiceCollection AddDeviceAdapters(this IServiceCollection services,
        ExecutionModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<IDeviceAdapter, DryRunDeviceAdapter>();
        services.AddSingleton<IAdapterSelector, AdapterSelector>();

        if (options.EffectiveMode != ExecutionMode.Live)
        {
            // Fine: in DRY_RUN non esiste alcun altro adapter registrato.
            return services;
        }

        // Fase 3: qui verranno registrati gli adapter reali, uno per uno, solo se presenti in
        // LiveEnabledAdapters. Finché la Fase 3 non è completata, LIVE non ha adapter reali
        // disponibili e il selettore ricade sul comportamento simulato.
        return services;
    }
}

/// <summary>
/// Seleziona l'adapter per un dispositivo. In DRY_RUN restituisce sempre quello simulato,
/// a prescindere dal protocollo configurato.
/// </summary>
public sealed class AdapterSelector(
    IEnumerable<IDeviceAdapter> adapters,
    ExecutionModeOptions options,
    ILogger<AdapterSelector> logger) : IAdapterSelector
{
    private readonly List<IDeviceAdapter> _adapters = [.. adapters];

    public ExecutionMode EffectiveMode => options.EffectiveMode;

    public IDeviceAdapter Select(ManagedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var dryRun = _adapters.FirstOrDefault(a => !a.PerformsRealOperations)
            ?? throw new InvalidOperationException(
                "Nessun adapter simulato registrato: la configurazione del contenitore è errata.");

        if (options.EffectiveMode != ExecutionMode.Live)
            return dryRun;

        var real = _adapters.FirstOrDefault(a =>
            a.PerformsRealOperations
            && a.CanHandle(device)
            && options.LiveEnabledAdapters.Contains(a.AdapterName, StringComparer.OrdinalIgnoreCase));

        if (real is not null) return real;

        // LIVE richiesto ma nessun adapter reale abilitato per questo dispositivo: si ricade sul
        // simulato e si registra il motivo, invece di fallire o di agire in modo imprevisto.
        logger.LogWarning(
            "Modalità LIVE attiva ma nessun adapter reale abilitato per «{Device}» " +
            "(protocollo {Protocol}): si utilizza l'adapter simulato.",
            device.Name, device.ShutdownProtocol);

        return dryRun;
    }
}
