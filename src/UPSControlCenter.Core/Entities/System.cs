using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Core.Entities;

public class NotificationChannel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType ChannelType { get; set; }
    public string ConfigJson { get; set; } = "{}";

    /// <summary>Segreto del canale cifrato (password SMTP, token webhook).</summary>
    public byte[]? SecretCiphertext { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Finestra di deduplicazione: eventi con la stessa chiave non vengono rinviati.</summary>
    public int CooldownSeconds { get; set; } = 300;

    public EventSeverity MinSeverity { get; set; } = EventSeverity.Warning;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class NotificationEvent
{
    public long Id { get; set; }
    public Guid NotificationChannelId { get; set; }
    public NotificationChannel? NotificationChannel { get; set; }

    public UpsEventType EventType { get; set; }

    /// <summary>Chiave di deduplicazione: tipo evento + bersaglio + finestra temporale.</summary>
    public string DedupKey { get; set; } = string.Empty;

    public ActionState State { get; set; } = ActionState.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
    public string? PayloadJson { get; set; }
}

/// <summary>Registro di audit append-only: nessun UPDATE o DELETE concesso (§4.4).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? IpAddress { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Motivazione testuale, obbligatoria per le operazioni sensibili (§8.2).</summary>
    public string? Reason { get; set; }
}

public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string ValueType { get; set; } = "string";
    public string? Description { get; set; }

    /// <summary>Se vero il valore non viene mai restituito dalle API né scritto nei log.</summary>
    public bool IsSensitive { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public static class SystemSettingKeys
{
    /// <summary>Modalità globale. Valore iniziale obbligatorio: <c>DryRun</c>.</summary>
    public const string ExecutionMode = "ExecutionMode";

    /// <summary>Elenco CSV degli adapter abilitati in LIVE. Valore iniziale: vuoto.</summary>
    public const string LiveEnabledAdapters = "LiveEnabledAdapters";

    public const string MeasurementRetentionDays = "MeasurementRetentionDays";
    public const string EventRetentionDays = "EventRetentionDays";
    public const string AuditRetentionDays = "AuditRetentionDays";
    public const string AllowedNetworks = "AllowedNetworks";
    public const string NutAccessMode = "NutAccessMode";
}
