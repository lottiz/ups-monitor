using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Abstractions;

/// <summary>Esito di un'operazione su un dispositivo gestito.</summary>
public sealed record AdapterResult
{
    public required bool Success { get; init; }
    public required string ResultCode { get; init; }
    public string? Message { get; init; }

    /// <summary>Vero se l'operazione è stata solo simulata. In DRY_RUN è sempre vero.</summary>
    public required bool WasDryRun { get; init; }

    public DeviceState? ObservedState { get; init; }

    public static AdapterResult Ok(string code, bool dryRun, string? message = null,
        DeviceState? state = null) =>
        new() { Success = true, ResultCode = code, WasDryRun = dryRun, Message = message, ObservedState = state };

    public static AdapterResult Fail(string code, bool dryRun, string? message = null) =>
        new() { Success = false, ResultCode = code, WasDryRun = dryRun, Message = message };
}

/// <summary>
/// Adapter per la gestione remota di un dispositivo.
/// <para>
/// In DRY_RUN l'unica implementazione registrata nel contenitore DI è quella simulata: gli adapter
/// reali non vengono nemmeno costruiti (§8.1). L'isolamento è un confine di composizione, non un
/// controllo condizionale dentro i metodi.
/// </para>
/// </summary>
public interface IDeviceAdapter
{
    /// <summary>Nome logico dell'adapter, usato per l'abilitazione LIVE selettiva e per l'audit.</summary>
    string AdapterName { get; }

    /// <summary>Vero se questo adapter effettua operazioni reali sulla rete.</summary>
    bool PerformsRealOperations { get; }

    bool CanHandle(ManagedDevice device);

    /// <summary>Spegnimento controllato. Mai forzato: §8.5 vieta il force-off automatico.</summary>
    Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default);

    Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default);

    /// <summary>
    /// Verifica lo stato. Un ping fallito <b>non</b> è prova di arresto (§10): l'implementazione
    /// deve restituire <see cref="DeviceState.Unreachable"/> e non <see cref="DeviceState.Off"/>
    /// quando l'unica evidenza è l'assenza di risposta.
    /// </summary>
    Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default);

    /// <summary>Test di connettività non distruttivo: non modifica lo stato del dispositivo.</summary>
    Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default);
}

/// <summary>
/// Seleziona l'adapter per un dispositivo. In DRY_RUN restituisce sempre l'adapter simulato,
/// indipendentemente dal protocollo configurato sul dispositivo.
/// </summary>
public interface IAdapterSelector
{
    IDeviceAdapter Select(ManagedDevice device);
    ExecutionMode EffectiveMode { get; }
}
