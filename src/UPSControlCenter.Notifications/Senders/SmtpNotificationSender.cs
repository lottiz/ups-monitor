using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Notifications.Senders;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public bool IsEnabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }

    /// <summary>Password SMTP. Proviene dall'ambiente, mai dal codice né dal repository (§15).</summary>
    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public IList<string> Recipients { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Invio via SMTP.
/// <para>
/// In Fase 1 il canale è predisposto ma disabilitato: l'invio effettivo richiede una libreria
/// SMTP con supporto STARTTLS moderno (<c>System.Net.Mail.SmtpClient</c> è deprecato per i nuovi
/// sviluppi). La scelta della libreria è rinviata alla Fase 5, dove le notifiche sono in scope,
/// per non introdurre ora una dipendenza che andrebbe poi sostituita.
/// </para>
/// </summary>
public sealed class SmtpNotificationSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpNotificationSender> logger) : INotificationSender
{
    public NotificationChannelType ChannelType => NotificationChannelType.Smtp;

    public Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!options.Value.IsEnabled)
        {
            logger.LogInformation(
                "[SMTP disabilitato] {Severity} {Event}: {Subject}",
                message.Severity, message.EventType, message.Subject);
            return Task.FromResult(SendResult.Ok());
        }

        logger.LogWarning(
            "Canale SMTP abilitato ma non ancora implementato (Fase 5). Notifica registrata " +
            "soltanto nel log: {Subject}", message.Subject);

        return Task.FromResult(SendResult.Fail(
            "Invio SMTP non implementato in questa fase: la notifica è stata registrata nel log."));
    }
}
