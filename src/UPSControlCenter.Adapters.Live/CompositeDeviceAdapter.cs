using Microsoft.Extensions.Logging;
using UPSControlCenter.Adapters;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Adapters.Live;

/// <summary>
/// Dispatcher verso gli adapter reali, con abilitazione granulare per singolo protocollo (§8.2).
/// <para>
/// Un dispositivo ha protocolli di spegnimento e di accensione indipendenti (es. SSH per
/// spegnere, Wake-on-LAN per accendere, §12): questa classe risolve il metodo giusto per
/// ciascuna azione, non un singolo adapter per l'intero dispositivo. L'abilitazione LIVE è
/// verificata <b>per ogni chiamata</b> contro l'elenco configurato: se il protocollo richiesto
/// non è abilitato, l'azione ricade sul simulato invece di eseguire comunque un'operazione
/// reale non autorizzata.
/// </para>
/// </summary>
public sealed class CompositeDeviceAdapter(
    IReadOnlyList<IDeviceAdapter> realAdapters,
    ExecutionModeOptions options,
    DryRunDeviceAdapter fallback,
    ILogger<CompositeDeviceAdapter> logger) : IDeviceAdapter
{
    public string AdapterName => "Composite";
    public bool PerformsRealOperations => true; // può eseguire operazioni reali, non lo fa sempre

    public bool CanHandle(ManagedDevice device) => true;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default) =>
        DispatchAsync(device, device.ShutdownProtocol.ToString(),
            a => a.SupportedShutdownProtocol == device.ShutdownProtocol,
            a => a.ShutdownAsync(device, ct), () => fallback.ShutdownAsync(device, ct));

    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default) =>
        DispatchAsync(device, device.StartupProtocol.ToString(),
            a => a.SupportedStartupProtocol == device.StartupProtocol,
            a => a.StartupAsync(device, ct), () => fallback.StartupAsync(device, ct));

    /// <summary>
    /// La verifica dello stato segue <see cref="ManagedDevice.StatusCheckMethod"/>, non il
    /// protocollo di spegnimento o accensione: sono due preoccupazioni indipendenti (§7).
    /// </summary>
    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        var (protocolName, predicate) = device.StatusCheckMethod switch
        {
            StatusCheckMethod.Ssh => (nameof(ShutdownProtocol.Ssh),
                (Func<IDeviceAdapter, bool>)(a => a.SupportedShutdownProtocol == ShutdownProtocol.Ssh)),
            StatusCheckMethod.Redfish => (nameof(ShutdownProtocol.Redfish),
                (Func<IDeviceAdapter, bool>)(a => a.SupportedShutdownProtocol == ShutdownProtocol.Redfish)),
            StatusCheckMethod.WinRm => (nameof(ShutdownProtocol.WinRm),
                (Func<IDeviceAdapter, bool>)(a => a.SupportedShutdownProtocol == ShutdownProtocol.WinRm)),
            _ => (null, null)
        };

        if (protocolName is null || predicate is null)
        {
            // Ping/TcpPort/HttpProbe/None/Composite: nessun adapter dedicato in questa fase
            // (Fase 3 copre SSH, Redfish, Wake-on-LAN). Lo stato resta onestamente ignoto invece
            // di essere inventato.
            return Task.FromResult(AdapterResult.Ok("CHECK_METHOD_NOT_IMPLEMENTED", dryRun: true,
                $"Metodo di verifica {device.StatusCheckMethod} non ancora implementato: " +
                "stato non determinabile.", DeviceState.Unknown));
        }

        return DispatchAsync(device, protocolName, predicate,
            a => a.CheckStateAsync(device, ct), () => fallback.CheckStateAsync(device, ct));
    }

    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default) =>
        DispatchAsync(device, device.ShutdownProtocol.ToString(),
            a => a.SupportedShutdownProtocol == device.ShutdownProtocol,
            a => a.TestConnectionAsync(device, ct), () => fallback.TestConnectionAsync(device, ct));

    private async Task<AdapterResult> DispatchAsync(ManagedDevice device, string protocolName,
        Func<IDeviceAdapter, bool> matches, Func<IDeviceAdapter, Task<AdapterResult>> invokeReal,
        Func<Task<AdapterResult>> invokeFallback)
    {
        var adapter = realAdapters.FirstOrDefault(matches);

        if (adapter is null)
        {
            // Nessun adapter reale implementa questo protocollo in questa fase del progetto.
            return await invokeFallback();
        }

        // Letto a ogni chiamata, non catturato alla costruzione: un'abilitazione decisa
        // dall'amministratore deve avere effetto senza riavviare il processo (§8.2). La
        // sincronizzazione periodica con il database è in ExecutionModeSyncService.
        if (!options.LiveEnabledAdapters.Contains(protocolName, StringComparer.OrdinalIgnoreCase))
        {
            // Protocollo non abilitato esplicitamente in LIVE: si ricade sul simulato invece di
            // eseguire un'operazione reale non autorizzata (§8.2). Non è un errore: è la
            // granularità per-protocollo che il progetto richiede.
            logger.LogInformation(
                "Protocollo {Protocol} non abilitato in LIVE per «{Device}»: azione simulata.",
                protocolName, device.Name);
            return await invokeFallback();
        }

        return await invokeReal(adapter);
    }
}
