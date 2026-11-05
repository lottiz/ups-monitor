using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Web.Controllers;

/// <summary>
/// Editor delle sequenze di spegnimento e riaccensione (§10, §12).
/// <para>
/// Un solo controller gestisce entrambe le direzioni tramite il parametro <c>kind</c>, perché
/// <see cref="ShutdownStep"/> e <see cref="StartupStep"/> sono strutturalmente identici per
/// quanto riguarda l'editor (differiscono solo nell'entità EF di destinazione): duplicare il
/// controller avrebbe significato duplicare anche ogni futura correzione.
/// </para>
/// </summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class SequencesController(
    UpsDbContext db,
    IAuditService audit,
    ILogger<SequencesController> logger) : Controller
{
    public IActionResult Index(SequenceKind kind)
    {
        ViewBag.Kind = kind;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> List(SequenceKind kind, CancellationToken ct)
    {
        if (kind == SequenceKind.Shutdown)
        {
            var sequences = await db.ShutdownSequences
                .AsNoTracking()
                .Select(s => new SequenceListItemDto
                {
                    Id = s.Id, Name = s.Name, Description = s.Description,
                    IsEnabled = s.IsEnabled, StepCount = s.Steps.Count
                })
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
            return Json(sequences);
        }

        var startup = await db.StartupSequences
            .AsNoTracking()
            .Select(s => new SequenceListItemDto
            {
                Id = s.Id, Name = s.Name, Description = s.Description,
                IsEnabled = s.IsEnabled, StepCount = s.Steps.Count
            })
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        return Json(startup);
    }

    [HttpGet]
    [Authorize(Policy = Policies.EditRules)]
    public IActionResult Create(SequenceKind kind)
    {
        ViewBag.Kind = kind;
        return View("Edit", new SequenceHeaderFormDto { Name = string.Empty });
    }

    [HttpGet]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> Edit(SequenceKind kind, Guid id, CancellationToken ct)
    {
        ViewBag.Kind = kind;

        if (kind == SequenceKind.Shutdown)
        {
            var seq = await db.ShutdownSequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (seq is null) return NotFound();
            return View(new SequenceHeaderFormDto { Id = seq.Id, Name = seq.Name, Description = seq.Description, IsEnabled = seq.IsEnabled });
        }

        var startup = await db.StartupSequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (startup is null) return NotFound();
        return View(new SequenceHeaderFormDto { Id = startup.Id, Name = startup.Name, Description = startup.Description, IsEnabled = startup.IsEnabled });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> SaveSequence(SequenceKind kind, SequenceHeaderFormDto model,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "Il nome è obbligatorio.");
            ViewBag.Kind = kind;
            return View("Edit", model);
        }

        Guid id;
        var isNew = model.Id is null;

        if (kind == SequenceKind.Shutdown)
        {
            ShutdownSequence seq;
            if (isNew) { seq = new ShutdownSequence { CreatedAt = DateTimeOffset.UtcNow }; db.ShutdownSequences.Add(seq); }
            else
            {
                var existing = await db.ShutdownSequences.FirstOrDefaultAsync(s => s.Id == model.Id, ct);
                if (existing is null) return NotFound();
                seq = existing;
            }
            seq.Name = model.Name; seq.Description = model.Description; seq.IsEnabled = model.IsEnabled;
            seq.UpdatedAt = DateTimeOffset.UtcNow;
            id = seq.Id;
        }
        else
        {
            StartupSequence seq;
            if (isNew) { seq = new StartupSequence { CreatedAt = DateTimeOffset.UtcNow }; db.StartupSequences.Add(seq); }
            else
            {
                var existing = await db.StartupSequences.FirstOrDefaultAsync(s => s.Id == model.Id, ct);
                if (existing is null) return NotFound();
                seq = existing;
            }
            seq.Name = model.Name; seq.Description = model.Description; seq.IsEnabled = model.IsEnabled;
            seq.UpdatedAt = DateTimeOffset.UtcNow;
            id = seq.Id;
        }

        await audit.RecordAsync(new AuditEntry
        {
            Action = $"{kind}Sequence.{(isNew ? "Create" : "Update")}",
            EntityType = kind == SequenceKind.Shutdown ? nameof(ShutdownSequence) : nameof(StartupSequence),
            EntityId = id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Outcome = "Success",
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { model.Name, model.IsEnabled })
        }, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Sequenza {Kind} «{Name}» {Action} da {User}.",
            kind, model.Name, isNew ? "creata" : "aggiornata", User.Identity?.Name);

        return RedirectToAction(nameof(EditSteps), new { kind, id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> DeleteSequence(SequenceKind kind, Guid id, CancellationToken ct)
    {
        if (kind == SequenceKind.Shutdown)
        {
            var seq = await db.ShutdownSequences.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (seq is null) return NotFound();
            db.ShutdownSequences.Remove(seq);
        }
        else
        {
            var seq = await db.StartupSequences.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (seq is null) return NotFound();
            db.StartupSequences.Remove(seq);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Sequenza eliminata." });
    }

    [HttpGet]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> EditSteps(SequenceKind kind, Guid id, CancellationToken ct)
    {
        ViewBag.Kind = kind;
        ViewBag.SequenceId = id;

        string? name = kind == SequenceKind.Shutdown
            ? (await db.ShutdownSequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct))?.Name
            : (await db.StartupSequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct))?.Name;

        if (name is null) return NotFound();
        ViewBag.SequenceName = name;

        ViewBag.Devices = await db.ManagedDevices
            .AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new SelectListItem(d.Name, d.Id.ToString())).ToListAsync(ct);
        ViewBag.Groups = await db.DeviceGroups
            .AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new SelectListItem(g.Name, g.Id.ToString())).ToListAsync(ct);

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Steps(SequenceKind kind, Guid sequenceId, CancellationToken ct)
    {
        if (kind == SequenceKind.Shutdown)
        {
            var steps = await db.ShutdownSteps
                .AsNoTracking()
                .Include(s => s.TargetDevice)
                .Include(s => s.TargetGroup)
                .Where(s => s.ShutdownSequenceId == sequenceId)
                .OrderBy(s => s.OrderIndex)
                .ToListAsync(ct);

            return Json(steps.Select(ToDto));
        }

        var startupSteps = await db.StartupSteps
            .AsNoTracking()
            .Include(s => s.TargetDevice)
            .Include(s => s.TargetGroup)
            .Where(s => s.StartupSequenceId == sequenceId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);

        return Json(startupSteps.Select(ToDto));
    }

    private static SequenceStepDto ToDto(ShutdownStep s) => new()
    {
        Id = s.Id, OrderIndex = s.OrderIndex, Name = s.Name, StepType = s.StepType,
        TargetDeviceId = s.TargetDeviceId, TargetDeviceName = s.TargetDevice?.Name,
        TargetGroupId = s.TargetGroupId, TargetGroupName = s.TargetGroup?.Name,
        ParallelGroup = s.ParallelGroup, DelayBeforeSeconds = s.DelayBeforeSeconds,
        TimeoutSeconds = s.TimeoutSeconds, MaxRetries = s.MaxRetries,
        ContinueOnError = s.ContinueOnError, VerifyCompletion = s.VerifyCompletion,
        ExcludedDeviceIds = s.ExcludedDeviceIds, IsEnabled = s.IsEnabled
    };

    private static SequenceStepDto ToDto(StartupStep s) => new()
    {
        Id = s.Id, OrderIndex = s.OrderIndex, Name = s.Name, StepType = s.StepType,
        TargetDeviceId = s.TargetDeviceId, TargetDeviceName = s.TargetDevice?.Name,
        TargetGroupId = s.TargetGroupId, TargetGroupName = s.TargetGroup?.Name,
        ParallelGroup = s.ParallelGroup, DelayBeforeSeconds = s.DelayBeforeSeconds,
        TimeoutSeconds = s.TimeoutSeconds, MaxRetries = s.MaxRetries,
        ContinueOnError = s.ContinueOnError, VerifyCompletion = s.VerifyCompletion,
        ExcludedDeviceIds = s.ExcludedDeviceIds, IsEnabled = s.IsEnabled
    };

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> SaveStep(SequenceKind kind, Guid sequenceId,
        [FromBody] SequenceStepDto model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.TargetDeviceId is null && model.TargetGroupId is null
            && model.StepType is Core.Enums.SequenceStepType.ShutdownDevice
                or Core.Enums.SequenceStepType.ShutdownGroup
                or Core.Enums.SequenceStepType.StartupDevice
                or Core.Enums.SequenceStepType.StartupGroup)
        {
            return BadRequest(new
            {
                message = "Uno step di spegnimento o accensione richiede un dispositivo o un gruppo bersaglio."
            });
        }

        // Gli indici di ordinamento duplicati non sono ammessi: il vincolo univoco del
        // database li rifiuterebbe comunque, ma qui si dà un messaggio comprensibile (§10).
        var isNew = model.Id is null;

        if (kind == SequenceKind.Shutdown)
        {
            var duplicateOrder = await db.ShutdownSteps.AnyAsync(s =>
                s.ShutdownSequenceId == sequenceId && s.OrderIndex == model.OrderIndex
                && s.Id != (model.Id ?? Guid.Empty), ct);
            if (duplicateOrder)
                return BadRequest(new { message = $"Esiste già uno step con ordine {model.OrderIndex}." });

            ShutdownStep step;
            if (isNew) { step = new ShutdownStep { ShutdownSequenceId = sequenceId }; db.ShutdownSteps.Add(step); }
            else
            {
                var existing = await db.ShutdownSteps.FirstOrDefaultAsync(s => s.Id == model.Id, ct);
                if (existing is null) return NotFound();
                step = existing;
            }
            MapInto(step, model);
        }
        else
        {
            var duplicateOrder = await db.StartupSteps.AnyAsync(s =>
                s.StartupSequenceId == sequenceId && s.OrderIndex == model.OrderIndex
                && s.Id != (model.Id ?? Guid.Empty), ct);
            if (duplicateOrder)
                return BadRequest(new { message = $"Esiste già uno step con ordine {model.OrderIndex}." });

            StartupStep step;
            if (isNew) { step = new StartupStep { StartupSequenceId = sequenceId }; db.StartupSteps.Add(step); }
            else
            {
                var existing = await db.StartupSteps.FirstOrDefaultAsync(s => s.Id == model.Id, ct);
                if (existing is null) return NotFound();
                step = existing;
            }
            MapInto(step, model);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Step salvato." });
    }

    private static void MapInto(ShutdownStep step, SequenceStepDto model)
    {
        step.OrderIndex = model.OrderIndex; step.Name = model.Name; step.StepType = model.StepType;
        step.TargetDeviceId = model.TargetDeviceId; step.TargetGroupId = model.TargetGroupId;
        step.ParallelGroup = model.ParallelGroup; step.DelayBeforeSeconds = model.DelayBeforeSeconds;
        step.TimeoutSeconds = model.TimeoutSeconds; step.MaxRetries = model.MaxRetries;
        step.ContinueOnError = model.ContinueOnError; step.VerifyCompletion = model.VerifyCompletion;
        step.ExcludedDeviceIds = [.. model.ExcludedDeviceIds]; step.IsEnabled = model.IsEnabled;
    }

    private static void MapInto(StartupStep step, SequenceStepDto model)
    {
        step.OrderIndex = model.OrderIndex; step.Name = model.Name; step.StepType = model.StepType;
        step.TargetDeviceId = model.TargetDeviceId; step.TargetGroupId = model.TargetGroupId;
        step.ParallelGroup = model.ParallelGroup; step.DelayBeforeSeconds = model.DelayBeforeSeconds;
        step.TimeoutSeconds = model.TimeoutSeconds; step.MaxRetries = model.MaxRetries;
        step.ContinueOnError = model.ContinueOnError; step.VerifyCompletion = model.VerifyCompletion;
        step.ExcludedDeviceIds = [.. model.ExcludedDeviceIds]; step.IsEnabled = model.IsEnabled;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> DeleteStep(SequenceKind kind, Guid stepId, CancellationToken ct)
    {
        if (kind == SequenceKind.Shutdown)
        {
            var step = await db.ShutdownSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
            if (step is null) return NotFound();
            db.ShutdownSteps.Remove(step);
        }
        else
        {
            var step = await db.StartupSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
            if (step is null) return NotFound();
            db.StartupSteps.Remove(step);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Step eliminato." });
    }

    /// <summary>
    /// Verifica l'assenza di dipendenze circolari fra i dispositivi coinvolti nella sequenza,
    /// usando lo stesso <see cref="DependencyGraph"/> dell'esecuzione reale (§10).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<DependencyValidationResultDto>> ValidateDependencies(
        SequenceKind kind, Guid sequenceId, CancellationToken ct)
    {
        List<Guid> deviceIds;

        if (kind == SequenceKind.Shutdown)
        {
            deviceIds = await db.ShutdownSteps
                .AsNoTracking()
                .Where(s => s.ShutdownSequenceId == sequenceId && s.TargetDeviceId != null)
                .Select(s => s.TargetDeviceId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            deviceIds = await db.StartupSteps
                .AsNoTracking()
                .Where(s => s.StartupSequenceId == sequenceId && s.TargetDeviceId != null)
                .Select(s => s.TargetDeviceId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        var dependencies = await db.DeviceDependencies
            .AsNoTracking()
            .Where(d => deviceIds.Contains(d.DependentDeviceId) || deviceIds.Contains(d.DependsOnDeviceId))
            .ToListAsync(ct);

        var graph = new DependencyGraph(deviceIds, dependencies);
        var validation = graph.Validate();

        if (!validation.IsValid)
            return Ok(new DependencyValidationResultDto { IsValid = false, Errors = validation.Errors });

        var order = kind == SequenceKind.Shutdown
            ? graph.TopologicalOrderForShutdown()
            : graph.TopologicalOrderForStartup();

        var names = await db.ManagedDevices
            .AsNoTracking()
            .Where(d => order.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return Ok(new DependencyValidationResultDto
        {
            IsValid = true,
            ShutdownOrderDeviceNames = [.. order.Select(id => names.GetValueOrDefault(id, id.ToString()))]
        });
    }
}
