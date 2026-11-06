using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Web.Services;

namespace UPSControlCenter.Web.Controllers.Api;

/// <summary>API di lettura per la dashboard e le tabelle Tabulator (§18).</summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Policies.ViewDashboard)]
[EnableRateLimiting("api")]
public sealed class DashboardApiController(
    DashboardStateService state,
    UpsDbContext db) : ControllerBase
{
    [HttpGet("ups")]
    public async Task<ActionResult<IReadOnlyList<UpsStatusDto>>> GetUps(CancellationToken ct) =>
        Ok(await state.GetUpsStatusAsync(ct));

    [HttpGet("infrastructure")]
    public async Task<ActionResult<InfrastructureStatusDto>> GetInfrastructure(CancellationToken ct) =>
        Ok(await state.GetInfrastructureStatusAsync(ct));

    [HttpGet("automation")]
    public async Task<ActionResult<AutomationStatusDto>> GetAutomation(CancellationToken ct) =>
        Ok(await state.GetAutomationStatusAsync(ct));

    /// <summary>
    /// Serie storica delle misure per i grafici, ridotta all'ultima lettura di ogni finestra
    /// richiesta (20 minuti per default). Il periodo è limitato a 30 giorni: un intervallo
    /// illimitato su un mini PC produrrebbe query pesanti e risposte inutilizzabili nel browser.
    /// </summary>
    [HttpGet("measurements/{upsId:guid}")]
    public async Task<ActionResult<IReadOnlyList<UpsMeasurementHistoryDto>>> GetMeasurements(
        Guid upsId,
        [FromQuery] int hours = 24,
        [FromQuery] int sampleMinutes = 20,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(hours, 1, 24 * 30);
        var clampedSampleMinutes = Math.Clamp(sampleMinutes, 1, 24 * 60);
        var since = DateTimeOffset.UtcNow.AddHours(-clamped);

        var readings = await db.UpsMeasurements
            .AsNoTracking()
            .Where(m => m.UpsDeviceId == upsId && m.RecordedAt >= since)
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

        var sampled = MeasurementSeriesSampler.Sample(
            readings,
            TimeSpan.FromMinutes(clampedSampleMinutes));

        return Ok(sampled);
    }

    /// <summary>Eventi recenti, per la pagina Eventi e il riquadro della dashboard.</summary>
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents([FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, 1000);

        var events = await db.UpsEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(clamped)
            .Select(e => new
            {
                e.Id,
                e.OccurredAt,
                EventType = e.EventType.ToString(),
                Severity = e.Severity.ToString(),
                e.BlackoutId,
                e.NewStatus,
                DetectedBy = e.DetectedBy.ToString()
            })
            .ToListAsync(ct);

        return Ok(events);
    }

    /// <summary>
    /// Variabili NUT grezze dell'ultima lettura, per la diagnostica. Mostra esattamente ciò che
    /// il driver fornisce, così è evidente quali parametri sono realmente disponibili (§9.2).
    /// </summary>
    [HttpGet("ups/{upsId:guid}/raw")]
    public async Task<IActionResult> GetRawVariables(Guid upsId, CancellationToken ct)
    {
        var latest = await db.UpsMeasurements
            .AsNoTracking()
            .Where(m => m.UpsDeviceId == upsId)
            .OrderByDescending(m => m.RecordedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
            return NotFound(new { message = "Nessuna misura disponibile per questo UPS." });

        var flags = (UpsStatusFlags)latest.StatusFlags;

        return Ok(new
        {
            latest.RecordedAt,
            latest.StatusRaw,
            StatusDecoded = flags.ToNutString(),
            latest.IsStale,
            // I campi assenti restano null: la UI li mostra come "non disponibile" (§5.1).
            Metrics = new
            {
                latest.BatteryChargePercent,
                latest.BatteryRuntimeSeconds,
                latest.BatteryVoltage,
                latest.InputVoltage,
                latest.OutputVoltage,
                latest.InputFrequency,
                latest.OutputFrequency,
                latest.LoadPercent,
                latest.RealPowerWatts
            },
            PhaseData = latest.PhaseDataJson
        });
    }
}
