using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;
using UPSControlCenter.Web.Services;

namespace UPSControlCenter.Web.Controllers;

/// <summary>Gestione degli UPS monitorati (§6).</summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class UpsController(
    UpsDbContext db,
    DashboardStateService state,
    IAuditService audit) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await state.GetUpsStatusAsync(ct));

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var ups = await db.UpsDevices.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (ups is null) return NotFound();

        var latest = await db.UpsMeasurements
            .AsNoTracking()
            .Where(m => m.UpsDeviceId == id)
            .OrderByDescending(m => m.RecordedAt)
            .FirstOrDefaultAsync(ct);

        ViewBag.Status = state.MapStatus(ups, latest);
        return View(ups);
    }

    /// <summary>
    /// Lettura live della corrente per l'analisi stanza per stanza (§6): si scollegano i
    /// dispositivi uno alla volta osservando la variazione di corrente, per stimarne
    /// l'assorbimento individuale.
    /// </summary>
    public async Task<IActionResult> LoadAnalysis(Guid id, CancellationToken ct)
    {
        var ups = await db.UpsDevices.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (ups is null) return NotFound();

        return View(ups);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<IActionResult> UpdateSettings(Guid id, int pollingIntervalSeconds,
        int communicationTimeoutSeconds, int staleDataThresholdSeconds, CancellationToken ct)
    {
        var ups = await db.UpsDevices.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (ups is null) return NotFound();

        // Gli stessi vincoli esistono nel database: qui servono per dare un messaggio utile
        // all'utente invece di una violazione di constraint.
        if (pollingIntervalSeconds is < 2 or > 300)
            return BadRequest(new { message = "L'intervallo di polling deve essere fra 2 e 300 secondi." });

        if (staleDataThresholdSeconds <= pollingIntervalSeconds)
            return BadRequest(new
            {
                message = "La soglia di obsolescenza deve essere maggiore dell'intervallo di " +
                          "polling, altrimenti ogni lettura risulterebbe obsoleta."
            });

        var before = new { ups.PollingIntervalSeconds, ups.CommunicationTimeoutSeconds, ups.StaleDataThresholdSeconds };

        ups.PollingIntervalSeconds = pollingIntervalSeconds;
        ups.CommunicationTimeoutSeconds = communicationTimeoutSeconds;
        ups.StaleDataThresholdSeconds = staleDataThresholdSeconds;
        ups.UpdatedAt = DateTimeOffset.UtcNow;

        await audit.RecordAsync(new AuditEntry
        {
            Action = "UpsDevice.UpdateSettings",
            EntityType = nameof(UpsDevice),
            EntityId = id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(before),
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ups.PollingIntervalSeconds, ups.CommunicationTimeoutSeconds, ups.StaleDataThresholdSeconds
            }),
            Outcome = "Success"
        }, ct);

        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Details), new { id });
    }
}
