using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Restituisce sempre <see cref="SimulatedDeviceAdapter"/>: il simulatore non deve poter
/// raggiungere un adapter reale per nessuna combinazione di protocollo o modalità (§14).
/// </summary>
public sealed class SimulatedAdapterSelector(SimulationScenarioDto scenario) : IAdapterSelector
{
    private readonly SimulatedDeviceAdapter _adapter = new(scenario);

    public ExecutionMode EffectiveMode => ExecutionMode.DryRun;

    public IDeviceAdapter Select(ManagedDevice device) => _adapter;
}
