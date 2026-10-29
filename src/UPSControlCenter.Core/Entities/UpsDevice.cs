using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

/// <summary>Un UPS fisico monitorato tramite NUT. Il sistema supporta più UPS (§6).</summary>
public class UpsDevice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string NutHost { get; set; } = "127.0.0.1";
    public int NutPort { get; set; } = 3493;

    /// <summary>Nome dell'UPS nella configurazione NUT (es. <c>riello</c>).</summary>
    public string NutUpsName { get; set; } = string.Empty;

    public int PollingIntervalSeconds { get; set; } = 10;
    public int CommunicationTimeoutSeconds { get; set; } = 30;

    /// <summary>Oltre questa età, una lettura è considerata obsoleta e non utilizzabile (§9.3).</summary>
    public int StaleDataThresholdSeconds { get; set; } = 60;

    // Popolati da NUT. Restano null se il driver non li fornisce: nessun valore inventato (§5.1).
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? FirmwareVersion { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<UpsMeasurement> Measurements { get; set; } = [];
    public ICollection<UpsEvent> Events { get; set; } = [];
    public ICollection<DevicePowerSource> PowerSources { get; set; } = [];
}

/// <summary>Misura puntuale. Tutte le metriche sono nullable: <c>null</c> = non fornito da NUT.</summary>
public class UpsMeasurement
{
    public long Id { get; set; }
    public Guid UpsDeviceId { get; set; }
    public UpsDevice? UpsDevice { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public string? StatusRaw { get; set; }
    public int StatusFlags { get; set; }

    public decimal? BatteryChargePercent { get; set; }
    public int? BatteryRuntimeSeconds { get; set; }
    public decimal? BatteryVoltage { get; set; }
    public decimal? InputVoltage { get; set; }
    public decimal? OutputVoltage { get; set; }
    public decimal? InputFrequency { get; set; }
    public decimal? OutputFrequency { get; set; }
    public decimal? LoadPercent { get; set; }
    public decimal? RealPowerWatts { get; set; }

    /// <summary>Dati trifase serializzati in JSON, presenti solo se il driver li espone.</summary>
    public string? PhaseDataJson { get; set; }

    public bool IsStale { get; set; }
    public WorkerRole Source { get; set; } = WorkerRole.Orchestrator;
}

public class UpsEvent
{
    public long Id { get; set; }
    public Guid? UpsDeviceId { get; set; }
    public UpsDevice? UpsDevice { get; set; }

    public UpsEventType EventType { get; set; }
    public EventSeverity Severity { get; set; }

    /// <summary>Correlazione con il blackout: identificativo deterministico condiviso tra i worker (§1.4).</summary>
    public string? BlackoutId { get; set; }

    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? DetailsJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public WorkerRole DetectedBy { get; set; }
}
