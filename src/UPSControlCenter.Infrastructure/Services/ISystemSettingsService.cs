using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Infrastructure.Services;

public interface ISystemSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<ExecutionMode> GetExecutionModeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLiveEnabledAdaptersAsync(CancellationToken ct = default);

    /// <summary>Aggiorna un'impostazione registrando sempre una voce di audit (§8.2).</summary>
    Task SetAsync(string key, string? value, string userName, string? reason,
        CancellationToken ct = default);
}

public sealed class SystemSettingsService(
    UpsDbContext db,
    IAuditService audit,
    ILogger<SystemSettingsService> logger) : ISystemSettingsService
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var setting = await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting?.Value;
    }

    /// <summary>
    /// Legge la modalità. Qualunque valore non riconosciuto ricade su DRY_RUN: un'impostazione
    /// corrotta non deve poter abilitare operazioni reali (§15).
    /// </summary>
    public async Task<ExecutionMode> GetExecutionModeAsync(CancellationToken ct = default)
    {
        var raw = await GetAsync(SystemSettingKeys.ExecutionMode, ct);
        if (Enum.TryParse<ExecutionMode>(raw, ignoreCase: true, out var mode))
            return mode;

        if (!string.IsNullOrWhiteSpace(raw))
            logger.LogWarning(
                "Valore non riconosciuto per {Key}: «{Value}». Si applica DRY_RUN.",
                SystemSettingKeys.ExecutionMode, raw);

        return ExecutionMode.DryRun;
    }

    public async Task<IReadOnlyList<string>> GetLiveEnabledAdaptersAsync(CancellationToken ct = default)
    {
        var raw = await GetAsync(SystemSettingKeys.LiveEnabledAdapters, ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public async Task SetAsync(string key, string? value, string userName, string? reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var before = setting?.Value;

        if (setting is null)
        {
            setting = new SystemSetting { Key = key, ValueType = "string" };
            db.SystemSettings.Add(setting);
        }

        setting.Value = value;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        setting.UpdatedBy = userName;

        // Il valore delle impostazioni sensibili non entra nell'audit in chiaro (§8.4).
        var sensitive = setting.IsSensitive;
        await audit.RecordAsync(new AuditEntry
        {
            Action = "SystemSetting.Update",
            EntityType = nameof(SystemSetting),
            EntityId = key,
            UserName = userName,
            BeforeJson = sensitive ? "\"***\"" : System.Text.Json.JsonSerializer.Serialize(before),
            AfterJson = sensitive ? "\"***\"" : System.Text.Json.JsonSerializer.Serialize(value),
            Outcome = "Success",
            Reason = reason
        }, ct);

        await db.SaveChangesAsync(ct);
    }
}
