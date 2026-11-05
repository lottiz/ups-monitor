using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;

namespace UPSControlCenter.Web.Controllers;

/// <summary>
/// Editor delle regole di spegnimento (§9.2). Opera sulla policy attiva: il progetto prevede
/// una sola policy attiva alla volta, garantita da un vincolo di unicità nel database (§4.3).
/// </summary>
[Authorize(Policy = Policies.ViewDashboard)]
public sealed class RulesController(
    UpsDbContext db,
    IAuditService audit,
    ILogger<RulesController> logger) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var policy = await db.ShutdownPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.IsActive, ct);
        ViewBag.PolicyName = policy?.Name;
        ViewBag.PolicyId = policy?.Id;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var policy = await db.ShutdownPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.IsActive, ct);
        if (policy is null) return Json(Array.Empty<ShutdownRuleListItemDto>());

        var rules = await db.ShutdownRules
            .AsNoTracking()
            .Include(r => r.TriggerSequence)
            .Where(r => r.ShutdownPolicyId == policy.Id)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        return Json(rules.Select(r => new ShutdownRuleListItemDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            Priority = r.Priority,
            IsEnabled = r.IsEnabled,
            TriggerSequenceName = r.TriggerSequence?.Name,
            HasTimeWindows = !string.IsNullOrWhiteSpace(r.TimeWindowsJson),
            HasDayRestriction = r.DaysOfWeekMask is { } m && m != 0
        }));
    }

    [HttpGet]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await PopulateSequencesAsync(ct);
        return View("Edit", new ShutdownRuleFormDto { Name = string.Empty });
    }

    [HttpGet]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var rule = await db.ShutdownRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        await PopulateSequencesAsync(ct);

        var tree = RuleSerialization.DeserializeCondition(rule.ConditionTreeJson);
        var simple = TryExtractSimpleForm(tree);

        var windows = RuleSerialization.DeserializeTimeWindows(rule.TimeWindowsJson);
        var windowsText = string.Join(Environment.NewLine,
            windows.Select(w => $"{w.Start:HH\\:mm}-{w.End:HH\\:mm}"));

        var days = new List<int>();
        if (rule.DaysOfWeekMask is { } mask)
        {
            for (var bit = 0; bit < 7; bit++)
                if ((mask & (1 << bit)) != 0) days.Add(bit);
        }

        return View(new ShutdownRuleFormDto
        {
            Id = rule.Id,
            Name = rule.Name,
            Description = rule.Description,
            Priority = rule.Priority,
            IsEnabled = rule.IsEnabled,
            TriggerSequenceId = rule.TriggerSequenceId,
            TopOperator = simple?.Top ?? NodeKind.And,
            SimpleConditions = simple?.Rows ?? [],
            UseAdvancedMode = simple is null,
            RawConditionJson = simple is null ? rule.ConditionTreeJson : null,
            TimeWindowsText = windowsText,
            SelectedDays = days
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> Save(ShutdownRuleFormDto model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);

        var policy = await db.ShutdownPolicies.FirstOrDefaultAsync(p => p.IsActive, ct);
        if (policy is null)
        {
            ModelState.AddModelError("", "Nessuna policy di spegnimento attiva.");
            await PopulateSequencesAsync(ct);
            return View("Edit", model);
        }

        // Costruzione dell'albero: la modalità avanzata ha precedenza se selezionata, altrimenti
        // si compone un AND/OR di primo livello dalle righe semplici (§9.2).
        ConditionNode? tree;
        if (model.UseAdvancedMode)
        {
            tree = RuleSerialization.DeserializeCondition(model.RawConditionJson);
            if (tree is null)
            {
                ModelState.AddModelError(nameof(model.RawConditionJson),
                    "Il JSON della condizione non è valido o non è deserializzabile.");
                await PopulateSequencesAsync(ct);
                return View("Edit", model);
            }
        }
        else
        {
            if (model.SimpleConditions.Count == 0)
            {
                ModelState.AddModelError("", "Aggiungere almeno una condizione.");
                await PopulateSequencesAsync(ct);
                return View("Edit", model);
            }

            var leaves = model.SimpleConditions.Select(BuildLeaf).ToArray();
            tree = leaves.Length == 1
                ? leaves[0]
                : (model.TopOperator == NodeKind.Or ? ConditionNode.Or(leaves) : ConditionNode.And(leaves));
        }

        if (!ModelState.IsValid)
        {
            await PopulateSequencesAsync(ct);
            return View("Edit", model);
        }

        var isNew = model.Id is null;
        ShutdownRule rule;

        if (isNew)
        {
            rule = new ShutdownRule { ShutdownPolicyId = policy.Id };
            db.ShutdownRules.Add(rule);
        }
        else
        {
            var existing = await db.ShutdownRules.FirstOrDefaultAsync(r => r.Id == model.Id, ct);
            if (existing is null) return NotFound();
            rule = existing;
        }

        var before = isNew ? null : System.Text.Json.JsonSerializer.Serialize(new
        {
            rule.Name, rule.Priority, rule.ConditionTreeJson, rule.IsEnabled
        });

        rule.Name = model.Name;
        rule.Description = model.Description;
        rule.Priority = model.Priority;
        rule.IsEnabled = model.IsEnabled;
        rule.TriggerSequenceId = model.TriggerSequenceId;
        rule.ConditionTreeJson = RuleSerialization.SerializeCondition(tree);

        var windowLines = (model.TimeWindowsText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TimeWindow.TryParse)
            .OfType<TimeWindow>()
            .ToList();
        rule.TimeWindowsJson = windowLines.Count > 0
            ? RuleSerialization.SerializeTimeWindows(windowLines)
            : null;

        rule.DaysOfWeekMask = model.SelectedDays.Count > 0
            ? model.SelectedDays.Aggregate(0, (mask, day) => mask | (1 << day))
            : null;

        await audit.RecordAsync(new AuditEntry
        {
            Action = isNew ? "ShutdownRule.Create" : "ShutdownRule.Update",
            EntityType = nameof(ShutdownRule),
            EntityId = rule.Id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = before,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                rule.Name, rule.Priority, rule.ConditionTreeJson, rule.IsEnabled
            }),
            Outcome = "Success"
        }, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Regola «{Rule}» {Action} da {User}.",
            rule.Name, isNew ? "creata" : "aggiornata", User.Identity?.Name);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.EditRules)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var rule = await db.ShutdownRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        db.ShutdownRules.Remove(rule);

        await audit.RecordAsync(new AuditEntry
        {
            Action = "ShutdownRule.Delete",
            EntityType = nameof(ShutdownRule),
            EntityId = id.ToString(),
            UserName = User.Identity?.Name,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { rule.Name }),
            Outcome = "Success"
        }, ct);

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Regola eliminata." });
    }

    /// <summary>
    /// Anteprima della valutazione: mostra il JSON canonico e un esito di validazione sintattica
    /// (non valuta contro uno stato UPS reale, quello è compito del simulatore, §14).
    /// </summary>
    [HttpPost]
    public IActionResult PreviewCondition([FromBody] ShutdownRuleFormDto model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ConditionNode? tree;
        if (model.UseAdvancedMode)
        {
            tree = RuleSerialization.DeserializeCondition(model.RawConditionJson);
        }
        else
        {
            var leaves = model.SimpleConditions.Select(BuildLeaf).ToArray();
            tree = leaves.Length == 0
                ? null
                : leaves.Length == 1
                    ? leaves[0]
                    : (model.TopOperator == NodeKind.Or ? ConditionNode.Or(leaves) : ConditionNode.And(leaves));
        }

        if (tree is null)
            return BadRequest(new { message = "Condizione non valida o vuota." });

        return Ok(new { json = RuleSerialization.SerializeCondition(tree) });
    }

    private static ConditionNode BuildLeaf(SimpleConditionRowDto row) => row.Condition switch
    {
        ConditionKind.UpsStatusFlag => ConditionNode.Flag(row.TextValue ?? "OB"),
        ConditionKind.TimeWindow => new ConditionNode
        {
            Kind = NodeKind.Leaf, Condition = ConditionKind.TimeWindow, TextValue = row.TextValue
        },
        ConditionKind.CommunicationLost or ConditionKind.DataStale or ConditionKind.BlackoutDurationUncertain
            => ConditionNode.Boolean(row.Condition),
        _ => ConditionNode.Numeric(row.Condition, row.Operator, row.NumericValue ?? 0)
    };

    /// <summary>
    /// Tenta di rappresentare l'albero come AND/OR di primo livello su sole foglie: se la
    /// struttura è più complessa (nidificazioni, NOT), restituisce <c>null</c> e l'editor
    /// ricade sulla modalità avanzata, senza perdere né alterare la condizione esistente.
    /// </summary>
    private static (NodeKind Top, List<SimpleConditionRowDto> Rows)? TryExtractSimpleForm(
        ConditionNode? tree)
    {
        if (tree is null) return (NodeKind.And, []);

        if (tree.IsLeaf) return (NodeKind.And, [ToRow(tree)]);

        if (tree.Kind is NodeKind.And or NodeKind.Or && tree.Children.All(c => c.IsLeaf))
            return (tree.Kind, tree.Children.Select(ToRow).ToList());

        return null;

        static SimpleConditionRowDto ToRow(ConditionNode leaf) => new()
        {
            Condition = leaf.Condition ?? ConditionKind.UpsStatusFlag,
            Operator = leaf.Operator,
            NumericValue = leaf.NumericValue,
            TextValue = leaf.TextValue
        };
    }

    private async Task PopulateSequencesAsync(CancellationToken ct)
    {
        ViewBag.Sequences = await db.ShutdownSequences
            .AsNoTracking()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new SelectListItem(s.Name, s.Id.ToString()))
            .ToListAsync(ct);
    }
}
