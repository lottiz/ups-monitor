using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Notifications;

public sealed record NotificationMessage
{
    public required UpsEventType EventType { get; init; }
    public required EventSeverity Severity { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public string? BlackoutId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Chiave di deduplicazione. Eventi con la stessa chiave entro la finestra di cooldown non
    /// vengono reinviati: durante un blackout lo stesso evento può ripetersi a ogni polling (§17).
    /// </summary>
    public required string DedupKey { get; init; }
}

public sealed record SendResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }

    public static SendResult Ok() => new() { Success = true };
    public static SendResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Canale di notifica. Le implementazioni non devono mai sollevare eccezioni verso il chiamante:
/// una notifica fallita non può interrompere una sequenza critica (§17).
/// </summary>
public interface INotificationSender
{
    NotificationChannelType ChannelType { get; }
    Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct = default);
}
