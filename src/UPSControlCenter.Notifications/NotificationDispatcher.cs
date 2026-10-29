using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;

namespace UPSControlCenter.Notifications;

/// <summary>
/// Distribuisce le notifiche applicando deduplicazione e cooldown.
/// <para>
/// L'invio è <b>fire-and-forget rispetto al chiamante</b>: il dispatcher accoda e restituisce
/// subito, così una sequenza di spegnimento non resta mai in attesa di un server SMTP lento (§17).
/// </para>
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotificationSender> senders,
    IClock clock,
    ILogger<NotificationDispatcher> logger)
{
    private readonly List<INotificationSender> _senders = [.. senders];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    /// <summary>Finestra predefinita di cooldown per chiave di deduplicazione.</summary>
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Accoda una notifica. Restituisce <c>false</c> se soppressa per deduplicazione.
    /// Non attende l'esito dell'invio.
    /// </summary>
    public bool Enqueue(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = clock.UtcNow;

        if (_lastSent.TryGetValue(message.DedupKey, out var last)
            && now - last < DefaultCooldown)
        {
            logger.LogDebug(
                "Notifica {Event} soppressa per deduplicazione (chiave {Key}, ultimo invio {Last}).",
                message.EventType, message.DedupKey, last);
            return false;
        }

        _lastSent[message.DedupKey] = now;

        // L'invio procede in background: gli errori sono registrati, non propagati.
        _ = Task.Run(() => SendToAllAsync(message, CancellationToken.None));
        return true;
    }

    private async Task SendToAllAsync(NotificationMessage message, CancellationToken ct)
    {
        foreach (var sender in _senders)
        {
            try
            {
                var result = await sender.SendAsync(message, ct);
                if (!result.Success)
                    logger.LogWarning(
                        "Invio su canale {Channel} non riuscito per l'evento {Event}: {Error}",
                        sender.ChannelType, message.EventType, result.Error);
            }
            catch (Exception ex)
            {
                // Catch ampio deliberato: nessun guasto di un canale di notifica deve propagarsi
                // al motore di orchestrazione.
                logger.LogError(ex,
                    "Errore non gestito nell'invio su canale {Channel} per l'evento {Event}.",
                    sender.ChannelType, message.EventType);
            }
        }
    }

    /// <summary>Numero di chiavi attualmente in cooldown, esposto per diagnostica e test.</summary>
    public int TrackedDedupKeys => _lastSent.Count;
}
