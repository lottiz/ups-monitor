using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Web.Controllers.Api;

/// <summary>
/// API consumate dal Safety Agent (§7.4).
/// <para>
/// L'agent <b>chiama</b> questi endpoint: non espone porte in ingresso. L'autenticazione è a
/// token condiviso perché l'agent è un servizio systemd senza sessione interattiva, e l'accesso
/// è ulteriormente limitato al loopback dal middleware di rete.
/// </para>
/// </summary>
[ApiController]
[Route("api/safety")]
[EnableRateLimiting("api")]
public sealed class SafetyController(
    UpsDbContext db,
    IClock clock,
    IConfiguration configuration,
    ILogger<SafetyController> logger) : ControllerBase
{
    private const string AgentTokenHeader = "X-Agent-Token";

    /// <summary>Restituisce la configurazione pubblicata, con checksum e firma da verificare.</summary>
    [HttpGet("config/current")]
    [AllowAnonymous]
    public async Task<ActionResult<SafetyConfigEnvelope>> GetCurrentConfig(CancellationToken ct)
    {
        if (!IsAgentAuthorized()) return Unauthorized();

        var published = await db.ConfigurationVersions
            .AsNoTracking()
            .Where(c => c.State == ConfigurationState.Published)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(ct);

        if (published is null)
        {
            // 404 e non un payload vuoto: l'agent deve distinguere "nessuna configurazione
            // pubblicata" da "configurazione vuota", che sono situazioni diverse.
            return NotFound(new { message = "Nessuna configurazione pubblicata." });
        }

        return Ok(new SafetyConfigEnvelope
        {
            Version = published.Version,
            CanonicalPayload = published.PayloadJson,
            ChecksumSha256 = published.ChecksumSha256,
            SignatureBase64 = Convert.ToBase64String(published.Signature),
            PublishedAt = published.PublishedAt ?? published.CreatedAt
        });
    }

    /// <summary>Conferma di ricezione da parte dell'agent.</summary>
    [HttpPost("config/{version:long}/ack")]
    [AllowAnonymous]
    public async Task<IActionResult> AcknowledgeConfig(long version,
        [FromBody] ConfigAckRequest request, CancellationToken ct)
    {
        if (!IsAgentAuthorized()) return Unauthorized();
        ArgumentNullException.ThrowIfNull(request);

        var config = await db.ConfigurationVersions
            .FirstOrDefaultAsync(c => c.Version == version, ct);

        if (config is null) return NotFound();

        config.AcknowledgedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Configurazione {Version} confermata in ricezione dall'agent {Instance}.",
            version, request.AgentInstanceId);

        return NoContent();
    }

    /// <summary>
    /// Conferma di attivazione. È distinta dalla ricezione: l'agent può avere scaricato una
    /// configurazione e averla poi rifiutata in validazione (§3.3).
    /// </summary>
    [HttpPost("config/{version:long}/activated")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmActivation(long version,
        [FromBody] ConfigAckRequest request, CancellationToken ct)
    {
        if (!IsAgentAuthorized()) return Unauthorized();
        ArgumentNullException.ThrowIfNull(request);

        var config = await db.ConfigurationVersions
            .FirstOrDefaultAsync(c => c.Version == version, ct);

        if (config is null) return NotFound();

        config.ActivatedAt = clock.UtcNow;
        config.ActivatedByAgent = request.AgentInstanceId;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Configurazione {Version} attivata dall'agent {Instance}.",
            version, request.AgentInstanceId);

        return NoContent();
    }

    /// <summary>
    /// Riceve l'heartbeat dell'agent. Aggiorna anche l'ultimo avvistamento, che la dashboard usa
    /// per mostrare lo stato del Safety Plane (§5.3).
    /// </summary>
    [HttpPost("heartbeat")]
    [AllowAnonymous]
    public async Task<IActionResult> Heartbeat([FromBody] AgentHeartbeatRequest request,
        CancellationToken ct)
    {
        if (!IsAgentAuthorized()) return Unauthorized();
        ArgumentNullException.ThrowIfNull(request);

        const string workerName = "ups-safety-agent";

        var heartbeat = await db.WorkerHeartbeats
            .FirstOrDefaultAsync(h => h.WorkerName == workerName, ct);

        if (heartbeat is null)
        {
            heartbeat = new WorkerHeartbeat { WorkerName = workerName };
            db.WorkerHeartbeats.Add(heartbeat);
        }

        heartbeat.InstanceId = request.InstanceId;
        heartbeat.LastSeenAt = clock.UtcNow;
        heartbeat.State = request.State;
        heartbeat.FencingToken = request.FencingToken;
        heartbeat.DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            request.ActiveConfigVersion,
            request.CurrentBlackoutId,
            request.BlackoutDurationSeconds,
            request.AutomationDisabled,
            request.LastUpsStatus
        });

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Verifica il token condiviso dell'agent con un confronto a tempo costante, per non
    /// esporre informazioni sul token attraverso i tempi di risposta.
    /// </summary>
    private bool IsAgentAuthorized()
    {
        var expected = configuration["SafetyApi:AgentToken"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            // Nessun token configurato: si nega l'accesso invece di aprire l'endpoint.
            logger.LogError(
                "SafetyApi:AgentToken non configurato: le chiamate dell'agent vengono rifiutate.");
            return false;
        }

        if (!Request.Headers.TryGetValue(AgentTokenHeader, out var provided)) return false;

        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided.ToString());
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);

        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(providedBytes, expectedBytes);
    }
}
