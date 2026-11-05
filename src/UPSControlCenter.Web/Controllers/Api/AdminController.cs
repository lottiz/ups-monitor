using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Web.Controllers.Api;

public sealed record KillSwitchRequest
{
    /// <summary>Motivazione obbligatoria: finisce nell'audit e nel file sentinella.</summary>
    public required string Reason { get; init; }
}

public sealed record EnableLiveRequest
{
    /// <summary>Nome dell'adapter da abilitare. L'abilitazione è per singolo adapter (§8.2).</summary>
    public required string AdapterName { get; init; }

    /// <summary>Conferma digitata dall'utente: deve corrispondere al nome dell'adapter.</summary>
    public required string Confirmation { get; init; }

    public required string Reason { get; init; }
}

public sealed record EnableLiveModeRequest
{
    /// <summary>Conferma digitata dall'utente: deve essere esattamente "LIVE".</summary>
    public required string Confirmation { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Operazioni amministrative sensibili (§8.2, §8.3).</summary>
[ApiController]
[Route("api/admin")]
[EnableRateLimiting("sensitive")]
public sealed class AdminController(
    IKillSwitch killSwitch,
    ISystemSettingsService settings,
    IAuditService audit,
    UpsDbContext db,
    IConfiguration configuration,
    ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>
    /// Kill switch: disabilita le automazioni mantenendo attivo il monitoraggio.
    /// Accessibile a Operator oltre che ad Administrator: è un'azione di sicurezza, e richiedere
    /// un amministratore durante un incidente sarebbe un ostacolo nel momento peggiore (§8.6).
    /// </summary>
    [HttpPost("automation/disable")]
    [Authorize(Policy = Policies.UseKillSwitch)]
    public async Task<IActionResult> DisableAutomation([FromBody] KillSwitchRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "La motivazione è obbligatoria." });

        var user = User.Identity?.Name ?? "sconosciuto";

        await killSwitch.DisableAsync(user, request.Reason, ct);

        // Oltre al file sentinella si riporta anche la modalità a DRY_RUN: la doppia azione
        // copre sia il Safety Agent (file locale) sia l'orchestrator (database).
        await settings.SetAsync(SystemSettingKeys.ExecutionMode, "DryRun", user,
            $"Kill switch: {request.Reason}", ct);
        await settings.SetAsync(SystemSettingKeys.LiveEnabledAdapters, "", user,
            $"Kill switch: {request.Reason}", ct);

        logger.LogWarning("Kill switch attivato da {User}: {Reason}", user, request.Reason);

        return Ok(new
        {
            message = "Automazioni disabilitate. Il monitoraggio resta attivo.",
            executionMode = "DryRun"
        });
    }

    [HttpPost("automation/enable")]
    [Authorize(Policy = Policies.EnableLive)]
    public async Task<IActionResult> EnableAutomation([FromBody] KillSwitchRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "La motivazione è obbligatoria." });

        var user = User.Identity?.Name ?? "sconosciuto";
        await killSwitch.EnableAsync(user, request.Reason, ct);

        // Rimuovere il blocco non riattiva la modalità LIVE: resta DRY_RUN finché non viene
        // richiesta esplicitamente con la procedura completa.
        return Ok(new
        {
            message = "Blocco rimosso. La modalità resta DRY_RUN fino ad abilitazione esplicita.",
            executionMode = await settings.GetExecutionModeAsync(ct)
        });
    }

    /// <summary>
    /// Attiva la modalità LIVE globale. Richiede: ruolo Administrator, conferma digitata
    /// ("LIVE"), motivazione, la variabile di deployment <c>UPS_ALLOW_LIVE=1</c>, kill switch
    /// non attivo (§8.2).
    /// <para>
    /// Da sola non autorizza alcuna operazione reale: senza almeno un adapter abilitato con
    /// <see cref="EnableLiveAdapter"/>, il dispatcher ricade comunque sul simulato per ogni
    /// dispositivo (§8.1). È la condizione "modalità richiesta", non "modalità sufficiente".
    /// </para>
    /// </summary>
    [HttpPost("live/enable-mode")]
    [Authorize(Policy = Policies.EnableLive)]
    [EnableRateLimiting("sensitive")]
    public async Task<IActionResult> EnableLiveMode([FromBody] EnableLiveModeRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = User.Identity?.Name ?? "sconosciuto";

        var deploymentAllows = configuration["UPS_ALLOW_LIVE"] == "1"
            || Environment.GetEnvironmentVariable("UPS_ALLOW_LIVE") == "1";

        if (!deploymentAllows)
        {
            await audit.RecordAsync(new AuditEntry
            {
                Action = "Live.EnableMode.Refused",
                EntityType = "ExecutionMode",
                UserName = user,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Outcome = "Refused",
                Reason = "Variabile di deployment UPS_ALLOW_LIVE assente."
            }, ct);
            await db.SaveChangesAsync(ct);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Modalità LIVE non consentita da questo deployment. " +
                          "Impostare UPS_ALLOW_LIVE=1 nell'ambiente dei servizi e riavviarli."
            });
        }

        if (!string.Equals(request.Confirmation, "LIVE", StringComparison.Ordinal))
            return BadRequest(new { message = "Digitare esattamente «LIVE» per confermare." });

        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "La motivazione è obbligatoria." });

        if (killSwitch.IsAutomationDisabled())
            return Conflict(new
            {
                message = "Kill switch attivo: rimuoverlo prima di attivare la modalità LIVE."
            });

        await settings.SetAsync(SystemSettingKeys.ExecutionMode, "Live", user, request.Reason, ct);

        logger.LogWarning("MODALITÀ LIVE ATTIVATA da {User}. Motivo: {Reason}", user, request.Reason);

        return Ok(new
        {
            message = "Modalità LIVE attivata. Nessuna operazione reale è ancora possibile senza " +
                      "abilitare almeno un adapter.",
            executionMode = "Live"
        });
    }

    /// <summary>
    /// Abilita un singolo adapter in modalità LIVE. Richiede: ruolo Administrator, conferma
    /// digitata, motivazione e la variabile di deployment <c>UPS_ALLOW_LIVE=1</c> (§8.2).
    /// </summary>
    [HttpPost("live/enable-adapter")]
    [Authorize(Policy = Policies.EnableLive)]
    public async Task<IActionResult> EnableLiveAdapter([FromBody] EnableLiveRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = User.Identity?.Name ?? "sconosciuto";

        // Barriera di deployment: senza di essa la richiesta è rifiutata e registrata. Impedisce
        // che un database ripristinato da un altro ambiente attivi operazioni reali (R18).
        var deploymentAllows = configuration["UPS_ALLOW_LIVE"] == "1"
            || Environment.GetEnvironmentVariable("UPS_ALLOW_LIVE") == "1";

        if (!deploymentAllows)
        {
            await audit.RecordAsync(new AuditEntry
            {
                Action = "Live.EnableAdapter.Refused",
                EntityType = "ExecutionMode",
                EntityId = request.AdapterName,
                UserName = user,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Outcome = "Refused",
                Reason = "Variabile di deployment UPS_ALLOW_LIVE assente."
            }, ct);
            await db.SaveChangesAsync(ct);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Modalità LIVE non consentita da questo deployment. " +
                          "Impostare UPS_ALLOW_LIVE=1 nell'ambiente del servizio e riavviarlo."
            });
        }

        if (!string.Equals(request.Confirmation, request.AdapterName, StringComparison.Ordinal))
            return BadRequest(new
            {
                message = "La conferma non corrisponde al nome dell'adapter."
            });

        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "La motivazione è obbligatoria." });

        if (killSwitch.IsAutomationDisabled())
            return Conflict(new
            {
                message = "Kill switch attivo: rimuoverlo prima di abilitare operazioni reali."
            });

        var current = (await settings.GetLiveEnabledAdaptersAsync(ct)).ToList();

        if (!current.Contains(request.AdapterName, StringComparer.OrdinalIgnoreCase))
            current.Add(request.AdapterName);

        await settings.SetAsync(SystemSettingKeys.LiveEnabledAdapters,
            string.Join(',', current), user, request.Reason, ct);

        logger.LogWarning(
            "Adapter {Adapter} ABILITATO in LIVE da {User}. Motivo: {Reason}",
            request.AdapterName, user, request.Reason);

        return Ok(new
        {
            message = $"Adapter «{request.AdapterName}» abilitato. " +
                      "La modalità globale LIVE richiede un'attivazione separata.",
            liveEnabledAdapters = current
        });
    }

    /// <summary>Riaccensioni in attesa di approvazione manuale (§12).</summary>
    [HttpGet("startup/pending")]
    [Authorize(Policy = Policies.ViewDashboard)]
    public async Task<IActionResult> GetPendingStartups(CancellationToken ct)
    {
        var pending = await db.OrchestrationRuns
            .AsNoTracking()
            .Where(r => r.RunKind == RunKind.Startup && r.State == RunState.Pending)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new
            {
                r.Id,
                r.BlackoutId,
                r.TriggerReason,
                r.StartedAt,
                r.ApprovedAt,
                r.ApprovedBy
            })
            .ToListAsync(ct);

        return Ok(pending);
    }

    /// <summary>
    /// Approva una riaccensione in attesa. Il Web e l'Orchestrator sono processi distinti: qui
    /// si registra solo l'approvazione, l'esecuzione effettiva viene rilevata e avviata
    /// dall'Orchestrator al ciclo di polling successivo (§12).
    /// </summary>
    [HttpPost("startup/{runId:guid}/approve")]
    [Authorize(Policy = Policies.UseKillSwitch)]
    [EnableRateLimiting("sensitive")]
    public async Task<IActionResult> ApproveStartup(Guid runId, CancellationToken ct)
    {
        var run = await db.OrchestrationRuns.FirstOrDefaultAsync(
            r => r.Id == runId && r.RunKind == RunKind.Startup && r.State == RunState.Pending, ct);

        if (run is null)
            return NotFound(new { message = "Riaccensione non trovata o non più in attesa." });

        var user = User.Identity?.Name ?? "sconosciuto";
        run.ApprovedAt = DateTimeOffset.UtcNow;
        run.ApprovedBy = user;

        await audit.RecordAsync(new AuditEntry
        {
            Action = "Startup.Approve",
            EntityType = nameof(OrchestrationRun),
            EntityId = runId.ToString(),
            UserName = user,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Outcome = "Success",
            Reason = run.TriggerReason
        }, ct);

        await db.SaveChangesAsync(ct);

        logger.LogWarning("Riaccensione {RunId} approvata da {User}.", runId, user);

        return Ok(new
        {
            message = "Riaccensione approvata: l'orchestrator la avvierà al prossimo ciclo di polling."
        });
    }
}
