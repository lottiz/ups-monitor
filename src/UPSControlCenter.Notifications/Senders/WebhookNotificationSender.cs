using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Notifications.Senders;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";

    public bool IsEnabled { get; set; }

    /// <summary>
    /// URL del webhook. Per Microsoft Teams va usato un flusso Power Automate / Workflows:
    /// i connettori Office 365 legacy sono deprecati da Microsoft (R21).
    /// </summary>
    public string? Url { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>Invio tramite webhook HTTPS. Vale anche per Teams via Workflows (§17).</summary>
public sealed class WebhookNotificationSender(
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookNotificationSender> logger) : INotificationSender
{
    public NotificationChannelType ChannelType => NotificationChannelType.Webhook;

    public async Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var opts = options.Value;

        if (!opts.IsEnabled || string.IsNullOrWhiteSpace(opts.Url))
        {
            logger.LogInformation(
                "[Webhook disabilitato] {Severity} {Event}: {Subject}",
                message.Severity, message.EventType, message.Subject);
            return SendResult.Ok();
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(WebhookNotificationSender));
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);

            var payload = new
            {
                eventType = message.EventType.ToString(),
                severity = message.Severity.ToString(),
                subject = message.Subject,
                body = message.Body,
                blackoutId = message.BlackoutId,
                occurredAt = message.OccurredAt
            };

            var response = await client.PostAsJsonAsync(opts.Url, payload, ct);

            return response.IsSuccessStatusCode
                ? SendResult.Ok()
                : SendResult.Fail($"Il webhook ha risposto {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // L'URL non viene registrato: può contenere un token di autenticazione (§8.4).
            logger.LogWarning(ex, "Invio webhook non riuscito per l'evento {Event}.", message.EventType);
            return SendResult.Fail(ex.Message);
        }
    }
}
