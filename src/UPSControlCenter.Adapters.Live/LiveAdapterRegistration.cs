using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Adapters.Live.Redfish;
using UPSControlCenter.Adapters.Live.Ssh;
using UPSControlCenter.Adapters.Live.WakeOnLan;
using UPSControlCenter.Adapters.Live.WinRm;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Adapters.Live;

/// <summary>
/// Selettore usato quando gli adapter reali sono registrati: in DRY_RUN restituisce comunque
/// sempre l'adapter simulato (lettura live di <see cref="ExecutionModeOptions.EffectiveMode"/>,
/// non una decisione presa alla costruzione), in LIVE restituisce il dispatcher composito.
/// </summary>
internal sealed class LiveAdapterSelector(
    CompositeDeviceAdapter composite,
    DryRunDeviceAdapter fallback,
    ExecutionModeOptions options) : IAdapterSelector
{
    public ExecutionMode EffectiveMode => options.EffectiveMode;

    public IDeviceAdapter Select(ManagedDevice device) =>
        options.EffectiveMode == ExecutionMode.Live ? composite : fallback;
}

/// <summary>
/// Registra gli adapter reali di Fase 3 (SSH, Redfish, Wake-on-LAN, WinRM) e il dispatcher che li
/// seleziona in base al protocollo e all'abilitazione per-adapter (§8.2).
/// <para>
/// Da chiamare <b>dopo</b> <c>AddDeviceAdapters</c> (che registra sempre l'adapter simulato) e
/// <b>solo</b> da Web e Orchestrator: il Safety Agent non la chiama mai, e resta quindi privo
/// di qualunque percorso verso operazioni reali in questa fase del progetto (§13).
/// </para>
/// <para>
/// Gli adapter vengono sempre registrati, anche se la modalità effettiva all'avvio è DRY_RUN:
/// l'abilitazione può cambiare a runtime tramite <see cref="ExecutionModeSyncService"/> senza
/// riavviare il processo, quindi gli adapter reali devono esistere nel contenitore da subito,
/// pronti per quando (e se) verranno autorizzati.
/// </para>
/// </summary>
public static class LiveAdapterRegistration
{
    public static IServiceCollection AddLiveDeviceAdapters(this IServiceCollection services)
    {
        services.AddSingleton<ISshSessionFactory, SshNetSessionFactory>();
        services.AddSingleton<IMagicPacketSender, UdpBroadcastMagicPacketSender>();
        services.AddSingleton<IWinRmSessionFactory, WinRmHttpSessionFactory>();

        services.AddSingleton<IDeviceAdapter>(sp => new SshDeviceAdapter(
            SshShutdownProfile.Linux,
            sp.GetRequiredService<ISshSessionFactory>(),
            sp.GetRequiredService<ICredentialProtector>(),
            sp.GetRequiredService<ILogger<SshDeviceAdapter>>()));

        services.AddSingleton<IDeviceAdapter>(sp => new SshDeviceAdapter(
            SshShutdownProfile.Qnap,
            sp.GetRequiredService<ISshSessionFactory>(),
            sp.GetRequiredService<ICredentialProtector>(),
            sp.GetRequiredService<ILogger<SshDeviceAdapter>>()));

        services.AddSingleton<IDeviceAdapter, RedfishDeviceAdapter>();
        services.AddSingleton<IDeviceAdapter, WakeOnLanStartupAdapter>();
        services.AddSingleton<IDeviceAdapter, WinRmShutdownAdapter>();

        services.AddSingleton<CompositeDeviceAdapter>(sp =>
        {
            var allAdapters = sp.GetServices<IDeviceAdapter>().ToList();
            var realAdapters = allAdapters.Where(a => a.PerformsRealOperations).ToList();
            return new CompositeDeviceAdapter(realAdapters, sp.GetRequiredService<ExecutionModeOptions>(),
                GetFallback(allAdapters), sp.GetRequiredService<ILogger<CompositeDeviceAdapter>>());
        });

        // Sostituisce il selettore DRY_RUN-only registrato da AddDeviceAdapters: la risoluzione
        // di un singolo servizio in Microsoft.Extensions.DependencyInjection restituisce
        // l'ultima registrazione, quindi questa prevale senza bisogno di rimuovere la precedente.
        services.AddSingleton<IAdapterSelector>(sp => new LiveAdapterSelector(
            sp.GetRequiredService<CompositeDeviceAdapter>(),
            GetFallback(sp.GetServices<IDeviceAdapter>().ToList()),
            sp.GetRequiredService<ExecutionModeOptions>()));

        services.AddHostedService<ExecutionModeSyncService>();

        return services;
    }

    private static DryRunDeviceAdapter GetFallback(IReadOnlyList<IDeviceAdapter> adapters) =>
        adapters.OfType<DryRunDeviceAdapter>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Nessun adapter simulato registrato: chiamare AddDeviceAdapters prima di " +
                "AddLiveDeviceAdapters.");
}
