using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

/// <summary>Dispositivo alimentato dall'UPS e gestito dal sistema (§7).</summary>
public class ManagedDevice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public DeviceType DeviceType { get; set; } = DeviceType.Generic;

    public Guid? DeviceGroupId { get; set; }
    public DeviceGroup? DeviceGroup { get; set; }

    /// <summary>Priorità di spegnimento: valori più bassi vengono trattati prima.</summary>
    public int Priority { get; set; } = 500;

    public ShutdownProtocol ShutdownProtocol { get; set; } = ShutdownProtocol.None;
    public StartupProtocol StartupProtocol { get; set; } = StartupProtocol.None;
    public int? ManagementPort { get; set; }

    public Guid? ConnectionProfileId { get; set; }
    public ConnectionProfile? ConnectionProfile { get; set; }

    /// <summary>MAC per Wake-on-LAN. La capability WoL non è assunta: va verificata (R19).</summary>
    public string? MacAddress { get; set; }

    public int ShutdownTimeoutSeconds { get; set; } = 300;
    public int StartupTimeoutSeconds { get; set; } = 600;

    /// <summary>Attesa dopo l'operazione, prima dello step successivo.</summary>
    public int PostActionDelaySeconds { get; set; } = 10;

    public StatusCheckMethod StatusCheckMethod { get; set; } = StatusCheckMethod.None;
    public string? StatusCheckTarget { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? Notes { get; set; }

    public DeviceState LastKnownState { get; set; } = DeviceState.Unknown;
    public DateTimeOffset? LastStateChangeAt { get; set; }

    /// <summary>
    /// Capability di accensione verificata con test non distruttivo. Finché è <c>null</c>
    /// (sconosciuta) il dispositivo non viene usato nelle sequenze di riaccensione (R19).
    /// </summary>
    public bool? StartupCapabilityVerified { get; set; }
    public DateTimeOffset? CapabilityVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Sorgenti di alimentazione. Un dispositivo può essere alimentato da più UPS: la perdita di
    /// una sola sorgente non implica assenza di alimentazione (§6).
    /// </summary>
    public ICollection<DevicePowerSource> PowerSources { get; set; } = [];

    public ICollection<DeviceDependency> Dependencies { get; set; } = [];
    public ICollection<DeviceDependency> Dependents { get; set; } = [];
}

/// <summary>Associazione dispositivo ↔ UPS. Modella l'alimentazione multi-sorgente.</summary>
public class DevicePowerSource
{
    public Guid ManagedDeviceId { get; set; }
    public ManagedDevice? ManagedDevice { get; set; }
    public Guid UpsDeviceId { get; set; }
    public UpsDevice? UpsDevice { get; set; }
    public bool IsPrimary { get; set; }
}

public class DeviceGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Priority { get; set; } = 500;
    public int ShutdownOrder { get; set; } = 500;
    public bool IsEnabled { get; set; } = true;
    public ICollection<ManagedDevice> Devices { get; set; } = [];
}

/// <summary>
/// Dipendenza fra dispositivi. L'insieme delle dipendenze forma un grafo che deve essere
/// aciclico: i cicli sono rifiutati in validazione (§10).
/// </summary>
public class DeviceDependency
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Dispositivo che dipende da un altro: va spento <b>prima</b> e acceso <b>dopo</b>.</summary>
    public Guid DependentDeviceId { get; set; }
    public ManagedDevice? DependentDevice { get; set; }

    public Guid DependsOnDeviceId { get; set; }
    public ManagedDevice? DependsOnDevice { get; set; }

    public DependencyType DependencyType { get; set; } = DependencyType.Generic;

    /// <summary>Se vera, la violazione della dipendenza blocca la sequenza invece di avvisare.</summary>
    public bool IsHard { get; set; } = true;
}

/// <summary>
/// Profilo di credenziali. Il segreto è conservato solo cifrato e non è mai esposto dalle API (§8.4).
/// </summary>
public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CredentialAuthType AuthType { get; set; } = CredentialAuthType.Password;
    public string Username { get; set; } = string.Empty;

    /// <summary>Segreto cifrato con Data Protection. Nessuna colonna in chiaro.</summary>
    public byte[]? SecretCiphertext { get; set; }

    /// <summary>Identificativo della chiave/purpose usata, per la rotazione.</summary>
    public string? SecretKeyId { get; set; }

    /// <summary>Fingerprint della host key o del certificato, verificato alla connessione.</summary>
    public string? Fingerprint { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ManagedDevice> Devices { get; set; } = [];
}
