using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Web.Services;

namespace UPSControlCenter.Web.Controllers;

/// <summary>
/// Report storico di carico, autonomia residua e altre metriche (§18), con periodo e
/// intervallo di campionamento scelti liberamente dall'utente.
/// </summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class ReportsController(UpsDbContext db, DisplayTimeFormatter displayTime) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewBag.UpsList = await db.UpsDevices
            .AsNoTracking()
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync(ct);

        return View();
    }

    /// <summary>
    /// Misure di un UPS nel periodo richiesto (giorni di calendario nel fuso dell'interfaccia,
    /// entrambi inclusi), ridotte a un campione ogni N minuti. Il periodo è limitato a 90 giorni:
    /// un intervallo illimitato su un mini PC produrrebbe query pesanti e risposte inutilizzabili
    /// nel browser (stesso vincolo dei grafici della dashboard).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid upsId, DateOnly from, DateOnly to,
        int sampleMinutes = 5, CancellationToken ct = default)
    {
        if (upsId == Guid.Empty || to < from)
            return BadRequest(new { message = "Periodo non valido." });

        var (fromUtc, toUtc) = ClampedPeriod(from, to);
        var clampedSampleMinutes = Math.Clamp(sampleMinutes, 1, 24 * 60);

        var readings = await db.UpsMeasurements
            .AsNoTracking()
            .Where(m => m.UpsDeviceId == upsId && m.RecordedAt >= fromUtc && m.RecordedAt < toUtc)
            .OrderBy(m => m.RecordedAt)
            .Select(m => new UpsMeasurementHistoryDto
            {
                RecordedAt = m.RecordedAt,
                BatteryChargePercent = m.BatteryChargePercent,
                BatteryRuntimeSeconds = m.BatteryRuntimeSeconds,
                LoadPercent = m.LoadPercent,
                InputVoltage = m.InputVoltage,
                OutputVoltage = m.OutputVoltage,
                StatusRaw = m.StatusRaw,
                IsStale = m.IsStale
            })
            .ToListAsync(ct);

        var sampled = MeasurementSeriesSampler.Sample(readings, TimeSpan.FromMinutes(clampedSampleMinutes));

        return Json(sampled);
    }

    /// <summary>
    /// Media di carico e autonomia residua nel periodo, calcolata sulle letture grezze (non sul
    /// campione mostrato in tabella) perché il campionamento tiene solo l'ultima lettura di ogni
    /// finestra e falserebbe la media su periodi lunghi. I valori assenti (§9.3) sono esclusi,
    /// non trattati come zero: <c>AVG</c> in SQL li ignora già di per sé.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Summary(Guid upsId, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        if (upsId == Guid.Empty || to < from)
            return BadRequest(new { message = "Periodo non valido." });

        var (fromUtc, toUtc) = ClampedPeriod(from, to);

        var query = db.UpsMeasurements
            .AsNoTracking()
            .Where(m => m.UpsDeviceId == upsId && m.RecordedAt >= fromUtc && m.RecordedAt < toUtc);

        var avgLoadPercent = await query.Select(m => m.LoadPercent).AverageAsync(ct);
        var avgBatteryRuntimeSeconds = await query.Select(m => (double?)m.BatteryRuntimeSeconds).AverageAsync(ct);
        var sampleCount = await query.CountAsync(ct);

        return Json(new
        {
            avgLoadPercent,
            avgBatteryRuntimeSeconds,
            sampleCount
        });
    }

    private (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ClampedPeriod(DateOnly from, DateOnly to)
    {
        var fromUtc = displayTime.ToUtcStartOfDay(from);
        var toUtc = displayTime.ToUtcStartOfDay(to.AddDays(1));

        var earliestAllowed = toUtc.AddDays(-90);
        if (fromUtc < earliestAllowed) fromUtc = earliestAllowed;

        return (fromUtc, toUtc);
    }
}
