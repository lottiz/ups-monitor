using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;
using UPSControlCenter.Web.ViewModels;

namespace UPSControlCenter.Web.Controllers;

/// <summary>CRUD completo dei dispositivi gestiti (§7).</summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class DevicesController(
    UpsDbContext db,
    IAdapterSelector adapterSelector,
    IAuditService audit,
    ILogger<DevicesController> logger) : Controller
{
    public IActionResult Index() => View();

    /// <summary>Dati per la tabella Tabulator (§18).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var devices = await db.ManagedDevices
            .AsNoTracking()
            .Include(d => d.DeviceGroup)
            .Include(d => d.ConnectionProfile)
            .Include(d => d.PowerSources)
                .ThenInclude(p => p.UpsDevice)
            .OrderBy(d => d.Priority).ThenBy(d => d.Name)
            .ToListAsync(ct);

        return Json(devices.Select(ToDto));
    }

    private static ManagedDeviceDto ToDto(ManagedDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        Hostname = d.Hostname,
        IpAddress = d.IpAddress,
        OperatingSystem = d.OperatingSystem,
        DeviceType = d.DeviceType,
        DeviceGroupId = d.DeviceGroupId,
        DeviceGroupName = d.DeviceGroup?.Name,
        Priority = d.Priority,
        ShutdownProtocol = d.ShutdownProtocol,
        StartupProtocol = d.StartupProtocol,
        ManagementPort = d.ManagementPort,
        ConnectionProfileId = d.ConnectionProfileId,
        ConnectionProfileName = d.ConnectionProfile?.Name,
        MacAddress = d.MacAddress,
        ShutdownTimeoutSeconds = d.ShutdownTimeoutSeconds,
        StartupTimeoutSeconds = d.StartupTimeoutSeconds,
        PostActionDelaySeconds = d.PostActionDelaySeconds,
        StatusCheckMethod = d.StatusCheckMethod,
        StatusCheckTarget = d.StatusCheckTarget,
        IsEnabled = d.IsEnabled,
        Notes = d.Notes,
        LastKnownState = d.LastKnownState,
        LastStateChangeAt = d.LastStateChangeAt,
        StartupCapabilityVerified = d.StartupCapabilityVerified,
        PowerSources = [.. d.PowerSources.Select(p => new PowerSourceDto
        {
            UpsDeviceId = p.UpsDeviceId,
            UpsName = p.UpsDevice?.Name ?? "(sconosciuto)",
            IsPrimary = p.IsPrimary
        })]
    };

    [HttpGet]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await PopulateListsAsync(ct);
        return View("Edit", new DeviceFormViewModel());
    }

    [HttpGet]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var device = await db.ManagedDevices
            .Include(d => d.PowerSources)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (device is null) return NotFound();

        await PopulateListsAsync(ct);

        return View(new DeviceFormViewModel
        {
            Id = device.Id,
            Name = device.Name,
            Description = device.Description,
            Hostname = device.Hostname,
            IpAddress = device.IpAddress,
            OperatingSystem = device.OperatingSystem,
            DeviceType = device.DeviceType,
            DeviceGroupId = device.DeviceGroupId,
            Priority = device.Priority,
            ShutdownProtocol = device.ShutdownProtocol,
            StartupProtocol = device.StartupProtocol,
            ManagementPort = device.ManagementPort,
            ConnectionProfileId = device.ConnectionProfileId,
            MacAddress = device.MacAddress,
            ShutdownTimeoutSeconds = device.ShutdownTimeoutSeconds,
            StartupTimeoutSeconds = device.StartupTimeoutSeconds,
            PostActionDelaySeconds = device.PostActionDelaySeconds,
            StatusCheckMethod = device.StatusCheckMethod,
            StatusCheckTarget = device.StatusCheckTarget,
            IsEnabled = device.IsEnabled,
            Notes = device.Notes,
            PowerSourceUpsIds = [.. device.PowerSources.Select(p => p.UpsDeviceId)],
            PrimaryUpsId = device.PowerSources.FirstOrDefault(p => p.IsPrimary)?.UpsDeviceId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<IActionResult> Save(DeviceFormViewModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!ModelState.IsValid)
        {
            await PopulateListsAsync(ct);
            return View("Edit", model);
        }

        // Un dispositivo abilitato senza protocollo non è gestibile: meglio bloccarlo qui che
        // scoprirlo durante un blackout.
        if (model.IsEnabled && model.ShutdownProtocol == Core.Enums.ShutdownProtocol.None)
        {
            ModelState.AddModelError(nameof(model.ShutdownProtocol),
                "Un dispositivo abilitato deve avere un protocollo di spegnimento.");
            await PopulateListsAsync(ct);
            return View("Edit", model);
        }

        var isNew = model.Id is null;
        ManagedDevice device;

        if (isNew)
        {
            device = new ManagedDevice { CreatedAt = DateTimeOffset.UtcNow };
            db.ManagedDevices.Add(device);
        }
        else
        {
            var existing = await db.ManagedDevices
                .Include(d => d.PowerSources)
                .FirstOrDefaultAsync(d => d.Id == model.Id, ct);

            if (existing is null) return NotFound();
            device = existing;
        }

        var before = isNew ? null : System.Text.Json.JsonSerializer.Serialize(ToDto(device));

        device.Name = model.Name;
        device.Description = model.Description;
        device.Hostname = model.Hostname;
        device.IpAddress = model.IpAddress;
        device.OperatingSystem = model.OperatingSystem;
        device.DeviceType = model.DeviceType;
        device.DeviceGroupId = model.DeviceGroupId;
        device.Priority = model.Priority;
        device.ShutdownProtocol = model.ShutdownProtocol;
        device.StartupProtocol = model.StartupProtocol;
        device.ManagementPort = model.ManagementPort;
        device.ConnectionProfileId = model.ConnectionProfileId;
        device.MacAddress = model.MacAddress;
        device.ShutdownTimeoutSeconds = model.ShutdownTimeoutSeconds;
        device.StartupTimeoutSeconds = model.StartupTimeoutSeconds;
        device.PostActionDelaySeconds = model.PostActionDelaySeconds;
        device.StatusCheckMethod = model.StatusCheckMethod;
        device.StatusCheckTarget = model.StatusCheckTarget;
        device.IsEnabled = model.IsEnabled;
        device.Notes = model.Notes;
        device.UpdatedAt = DateTimeOffset.UtcNow;

        SyncPowerSources(device, model);

        await audit.RecordAsync(new AuditEntry
        {
            Action = isNew ? "ManagedDevice.Create" : "ManagedDevice.Update",
            EntityType = nameof(ManagedDevice),
            EntityId = device.Id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = before,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                device.Name, device.Hostname, device.DeviceType, device.IsEnabled
            }),
            Outcome = "Success"
        }, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Dispositivo «{Name}» {Action} da {User}.",
            device.Name, isNew ? "creato" : "aggiornato", User.Identity?.Name);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Allinea le sorgenti di alimentazione. Le voci rimosse vengono eliminate e quelle nuove
    /// aggiunte, invece di cancellare e ricreare tutto: evita churn inutile sulle chiavi.
    /// </summary>
    private void SyncPowerSources(ManagedDevice device, DeviceFormViewModel model)
    {
        var wanted = model.PowerSourceUpsIds.Distinct().ToHashSet();

        foreach (var existing in device.PowerSources.ToList())
        {
            if (!wanted.Contains(existing.UpsDeviceId))
                device.PowerSources.Remove(existing);
        }

        foreach (var upsId in wanted)
        {
            var current = device.PowerSources.FirstOrDefault(p => p.UpsDeviceId == upsId);
            if (current is null)
            {
                device.PowerSources.Add(new DevicePowerSource
                {
                    ManagedDeviceId = device.Id,
                    UpsDeviceId = upsId,
                    IsPrimary = model.PrimaryUpsId == upsId
                });
            }
            else
            {
                current.IsPrimary = model.PrimaryUpsId == upsId;
            }
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var device = await db.ManagedDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        // Le dipendenze vanno rimosse prima: un dispositivo citato da altri non può sparire
        // lasciando riferimenti pendenti nelle sequenze.
        var dependencies = await db.DeviceDependencies
            .Where(d => d.DependentDeviceId == id || d.DependsOnDeviceId == id)
            .ToListAsync(ct);

        if (dependencies.Count > 0)
        {
            return Conflict(new
            {
                message = $"Il dispositivo è coinvolto in {dependencies.Count} dipendenze. " +
                          "Rimuoverle prima di eliminarlo."
            });
        }

        db.ManagedDevices.Remove(device);

        await audit.RecordAsync(new AuditEntry
        {
            Action = "ManagedDevice.Delete",
            EntityType = nameof(ManagedDevice),
            EntityId = id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { device.Name, device.Hostname }),
            Outcome = "Success"
        }, ct);

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Dispositivo eliminato." });
    }

    /// <summary>
    /// Test di connessione non distruttivo. In DRY_RUN non apre alcuna connessione: lo dichiara
    /// esplicitamente nella risposta invece di restituire un falso successo (§7, §8.1).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.ManageDevices)]
    public async Task<ActionResult<ConnectionTestResultDto>> TestConnection(Guid id,
        CancellationToken ct)
    {
        var device = await db.ManagedDevices
            .Include(d => d.ConnectionProfile)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (device is null) return NotFound();

        var adapter = adapterSelector.Select(device);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await adapter.TestConnectionAsync(device, ct);
        stopwatch.Stop();

        await audit.RecordAsync(new AuditEntry
        {
            Action = "ManagedDevice.TestConnection",
            EntityType = nameof(ManagedDevice),
            EntityId = id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Outcome = result.Success ? "Success" : "Failed",
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                adapter.AdapterName, result.ResultCode, result.WasDryRun
            })
        }, ct);

        await db.SaveChangesAsync(ct);

        return Ok(new ConnectionTestResultDto
        {
            Success = result.Success,
            AdapterName = adapter.AdapterName,
            WasDryRun = result.WasDryRun,
            ResultCode = result.ResultCode,
            Message = result.Message,
            ObservedState = result.ObservedState,
            DurationMilliseconds = (int)stopwatch.ElapsedMilliseconds
        });
    }

    private async Task PopulateListsAsync(CancellationToken ct)
    {
        ViewBag.Groups = await db.DeviceGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new SelectListItem(g.Name, g.Id.ToString()))
            .ToListAsync(ct);

        ViewBag.Profiles = await db.ConnectionProfiles
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Name)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToListAsync(ct);

        ViewBag.UpsDevices = await db.UpsDevices
            .AsNoTracking()
            .OrderBy(u => u.Name)
            .Select(u => new SelectListItem(u.Name, u.Id.ToString()))
            .ToListAsync(ct);
    }
}
