using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Infrastructure.Adapters;

/// <summary>
/// Adapter simulato: registra ciò che <b>avrebbe</b> fatto, senza alcuna operazione di rete.
/// <para>
/// Non apre socket, non risolve nomi, non esegue processi. È l'unico adapter registrato in
/// DRY_RUN (§8.1) e non ha percorsi di codice che eseguano I/O verso i dispositivi.
/// </para>
/// </summary>
public sealed class DryRunDeviceAdapter(ILogger<DryRunDeviceAdapter> logger) : IDeviceAdapter
{
    public const string Name = "DryRun";

    public string AdapterName => Name;

    /// <summary>Sempre falso: è la proprietà che i test usano per verificare l'isolamento.</summary>
    public bool PerformsRealOperations => false;

    /// <summary>Gestisce qualunque dispositivo: in DRY_RUN sostituisce ogni adapter reale.</summary>
    public bool CanHandle(ManagedDevice device) => true;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        logger.LogInformation(
            "[DRY_RUN] Spegnimento simulato di «{Device}» ({Hostname}) tramite {Protocol}. " +
            "Nessun comando inviato.",
            device.Name, device.Hostname, device.ShutdownProtocol);

        return Task.FromResult(AdapterResult.Ok("DRY_RUN_SHUTDOWN", dryRun: true,
            $"Spegnimento simulato tramite {device.ShutdownProtocol}: nessun comando reale inviato.",
            DeviceState.ShuttingDown));
    }

    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        logger.LogInformation(
            "[DRY_RUN] Accensione simulata di «{Device}» ({Hostname}) tramite {Protocol}. " +
            "Nessun pacchetto inviato.",
            device.Name, device.Hostname, device.StartupProtocol);

        return Task.FromResult(AdapterResult.Ok("DRY_RUN_STARTUP", dryRun: true,
            $"Accensione simulata tramite {device.StartupProtocol}: nessun pacchetto reale inviato.",
            DeviceState.Starting));
    }

    /// <summary>
    /// Verifica simulata. Restituisce <see cref="DeviceState.Unknown"/> e non uno stato inventato:
    /// in DRY_RUN non esiste alcuna evidenza reale sullo stato del dispositivo.
    /// </summary>
    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        logger.LogDebug("[DRY_RUN] Verifica stato simulata di «{Device}».", device.Name);

        return Task.FromResult(AdapterResult.Ok("DRY_RUN_CHECK", dryRun: true,
            "Verifica simulata: in DRY_RUN lo stato reale del dispositivo non è osservabile.",
            DeviceState.Unknown));
    }

    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        logger.LogInformation("[DRY_RUN] Test di connessione simulato verso «{Device}».", device.Name);

        return Task.FromResult(AdapterResult.Ok("DRY_RUN_TEST", dryRun: true,
            "Test simulato: nessuna connessione aperta. Per verificare la raggiungibilità reale " +
            "è necessario abilitare l'adapter corrispondente in modalità LIVE.",
            DeviceState.Unknown));
    }
}
