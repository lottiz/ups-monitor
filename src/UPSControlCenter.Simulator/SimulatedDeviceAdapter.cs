using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Adapter fittizio del simulatore. Oltre a non eseguire operazioni reali, riproduce i guasti
/// previsti dallo scenario: dispositivi irraggiungibili e timeout di spegnimento (§14).
/// </summary>
public sealed class SimulatedDeviceAdapter(SimulationScenarioDto scenario) : IDeviceAdapter
{
    public string AdapterName => "Simulated";

    /// <summary>Sempre falso. Il simulatore non può raggiungere adapter reali per costruzione.</summary>
    public bool PerformsRealOperations => false;

    public bool CanHandle(ManagedDevice device) => true;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (scenario.UnreachableDeviceIds.Contains(device.Id))
            return Task.FromResult(AdapterResult.Fail("SIM_UNREACHABLE", dryRun: true,
                $"Dispositivo «{device.Name}» non raggiungibile (simulato)."));

        if (scenario.TimeoutDeviceIds.Contains(device.Id))
            return Task.FromResult(AdapterResult.Fail("SIM_TIMEOUT", dryRun: true,
                $"Timeout di spegnimento per «{device.Name}» dopo " +
                $"{device.ShutdownTimeoutSeconds}s (simulato)."));

        return Task.FromResult(AdapterResult.Ok("SIM_SHUTDOWN", dryRun: true,
            $"Spegnimento simulato di «{device.Name}».", DeviceState.Off));
    }

    public Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (scenario.UnreachableDeviceIds.Contains(device.Id))
            return Task.FromResult(AdapterResult.Fail("SIM_UNREACHABLE", dryRun: true,
                $"Dispositivo «{device.Name}» non raggiungibile per l'accensione (simulato)."));

        return Task.FromResult(AdapterResult.Ok("SIM_STARTUP", dryRun: true,
            $"Accensione simulata di «{device.Name}».", DeviceState.On));
    }

    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        // Un dispositivo irraggiungibile risulta Unreachable, non Off: l'assenza di risposta non
        // è prova di arresto (§10).
        if (scenario.UnreachableDeviceIds.Contains(device.Id))
            return Task.FromResult(AdapterResult.Ok("SIM_CHECK", dryRun: true,
                "Nessuna risposta: stato non determinabile.", DeviceState.Unreachable));

        return Task.FromResult(AdapterResult.Ok("SIM_CHECK", dryRun: true,
            "Verifica simulata.", DeviceState.Off));
    }

    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Ok("SIM_TEST", dryRun: true,
            "Test simulato: nessuna connessione aperta."));
}
