using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Contracts.Dto;

public sealed record ManagedDeviceDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Hostname { get; init; }
    public string? IpAddress { get; init; }
    public string? OperatingSystem { get; init; }
    public DeviceType DeviceType { get; init; }
    public Guid? DeviceGroupId { get; init; }
    public string? DeviceGroupName { get; init; }
    public int Priority { get; init; }
    public ShutdownProtocol ShutdownProtocol { get; init; }
    public StartupProtocol StartupProtocol { get; init; }
    public int? ManagementPort { get; init; }
    public Guid? ConnectionProfileId { get; init; }
    public string? ConnectionProfileName { get; init; }
    public string? MacAddress { get; init; }
    public int ShutdownTimeoutSeconds { get; init; }
    public int StartupTimeoutSeconds { get; init; }
    public int PostActionDelaySeconds { get; init; }
    public StatusCheckMethod StatusCheckMethod { get; init; }
    public string? StatusCheckTarget { get; init; }
    public bool IsEnabled { get; init; }
    public string? Notes { get; init; }
    public DeviceState LastKnownState { get; init; }
    public DateTimeOffset? LastStateChangeAt { get; init; }
    public bool? StartupCapabilityVerified { get; init; }

    /// <summary>UPS che alimentano il dispositivo: può essere più di uno (§6).</summary>
    public IReadOnlyList<PowerSourceDto> PowerSources { get; init; } = [];
}

public sealed record PowerSourceDto
{
    public required Guid UpsDeviceId { get; init; }
    public required string UpsName { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>
/// Esito di un test di connessione. Il test è non distruttivo: non modifica lo stato del
/// dispositivo e in DRY_RUN non apre nemmeno connessioni reali (§7, §8.1).
/// </summary>
public sealed record ConnectionTestResultDto
{
    public required bool Success { get; init; }
    public required string AdapterName { get; init; }
    public required bool WasDryRun { get; init; }
    public required string ResultCode { get; init; }
    public string? Message { get; init; }
    public DeviceState? ObservedState { get; init; }
    public int DurationMilliseconds { get; init; }
}
