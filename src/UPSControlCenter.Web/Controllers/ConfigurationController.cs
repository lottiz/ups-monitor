using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Web.Controllers;

/// <summary>Gestione delle versioni di configurazione distribuite al Safety Agent (§3.3).</summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class ConfigurationController(
    UpsDbContext db,
    IConfigurationPublisher publisher) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var versions = await db.ConfigurationVersions
            .AsNoTracking()
            .OrderByDescending(c => c.Version)
            .Take(100)
            .Select(c => new
            {
                c.Version,
                c.State,
                c.CreatedBy,
                c.CreatedAt,
                c.PublishedAt,
                c.AcknowledgedAt,
                c.ActivatedAt,
                c.ActivatedByAgent,
                c.ChecksumSha256,
                c.ValidationErrorsJson
            })
            .ToListAsync(ct);

        return View(versions);
    }

    /// <summary>
    /// Mostra l'anteprima della configurazione che verrebbe pubblicata, con l'esito della
    /// validazione: l'utente deve poter vedere il contenuto prima di distribuirlo all'agent.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Policies.PublishConfiguration)]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var config = await publisher.BuildAsync(ct);

        if (config is null)
            return Ok(new
            {
                canPublish = false,
                message = "Impossibile costruire la configurazione: servono una policy di " +
                          "spegnimento attiva e almeno un UPS abilitato."
            });

        var validator = new Core.Safety.SafetyConfigurationValidator();
        var validation = validator.Validate(config);

        return Ok(new
        {
            canPublish = validation.IsValid,
            validation.Errors,
            validation.Warnings,
            config.Version,
            config.ExecutionMode,
            deviceCount = config.Devices.Count,
            stepCount = config.ShutdownSteps.Count,
            payload = Core.Safety.ConfigurationIntegrity.Canonicalize(config)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.PublishConfiguration)]
    [EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Publish(string? reason, CancellationToken ct)
    {
        var user = User.Identity?.Name ?? "sconosciuto";
        var result = await publisher.PublishAsync(user, reason, ct);

        if (!result.Success)
            return BadRequest(new { result.Errors, result.Warnings });

        return Ok(new
        {
            message = $"Configurazione versione {result.Version} pubblicata. " +
                      "Il Safety Agent la applicherà alla prossima sincronizzazione.",
            result.Version,
            result.Warnings
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.PublishConfiguration)]
    [EnableRateLimiting("sensitive")]
    public async Task<IActionResult> Rollback(long version, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "La motivazione del rollback è obbligatoria." });

        var user = User.Identity?.Name ?? "sconosciuto";
        var result = await publisher.RollbackToAsync(version, user, reason, ct);

        if (!result.Success)
            return BadRequest(new { result.Errors });

        return Ok(new
        {
            message = $"Rollback eseguito: il payload della versione {version} è stato " +
                      $"ripubblicato come versione {result.Version}.",
            result.Version
        });
    }
}
