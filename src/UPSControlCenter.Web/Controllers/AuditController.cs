using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Web.Controllers;

/// <summary>
/// Consultazione del registro di audit (§18). Solo lettura: l'audit è append-only e nessun
/// ruolo può modificarlo o cancellarlo dall'interfaccia (§4.4).
/// </summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class AuditController(UpsDbContext db) : Controller
{
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit, CancellationToken ct)
    {
        var clamped = Math.Clamp(limit <= 0 ? 200 : limit, 1, 2000);

        var entries = await db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(a => a.OccurredAt)
            .Take(clamped)
            .Select(a => new
            {
                a.Id,
                a.OccurredAt,
                a.UserName,
                a.IpAddress,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.Outcome,
                a.Reason
            })
            .ToListAsync(ct);

        return Json(entries);
    }
}
