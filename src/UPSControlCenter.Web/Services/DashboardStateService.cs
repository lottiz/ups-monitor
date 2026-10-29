using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;
using UPSControlCenter.Web.Hubs;

namespace UPSControlCenter.Web.Services;

/// <summary>
/// Compone lo stato mostrato dalla dashboard a partire dal database.
/// <para>
/// Il portale <b>non interroga NUT direttamente</b>: legge le misure scritte dall'orchestrator.
/// Così la dashboard non introduce un secondo consumatore su <c>upsd</c> e, se l'orchestrator è
/// giù, il dato risulta onestamente obsoleto invece di sembrare aggiornato (§5.3).
/// </para>
/// </summary>
public sealed class DashboardStateService(
    UpsDbContext db,
    IKillSwitch killSwitch,
    IClock clock,
    IHubContext<DashboardHub, IDashboardClient> hub)
{
    /// <summary>Nome con cui l'orchestrator registra il proprio heartbeat.</summary>
    private const string OrchestratorWorkerName = "ups-orchestrator";
    private const string SafetyAgentWorkerName = "ups-safety-agent";

    public async Task<IReadOnlyList<UpsStatusDto>> GetUpsStatusAsync(CancellationToken ct = default)
    {
        var devices = await db.UpsDevices
            .AsNoTracking()
            .Where(u => u.IsEnabled)
            .OrderBy(u => u.Name)
            .ToListAsync(ct);

        var result = new List<UpsStatusDto>(devices.Count);

        foreach (var ups in devices)
        {
            var latest = await db.UpsMeasurements
                .AsNoTracking()
                .Where(m => m.UpsDeviceId == ups.Id)
                .OrderByDescending(m => m.RecordedAt)
                .FirstOrDefaultAsync(ct);

            result.Add(MapStatus(ups, latest));
        }

        return result;
    }

    internal UpsStatusDto MapStatus(UpsDevice ups, UpsMeasurement? latest)
    {
        if (latest is null)
        {
            // Nessuna misura: lo si dichiara, non si mostrano zeri (§5.1).
            return new UpsStatusDto
            {
                Id = ups.Id,
                Name = ups.Name,
                Manufacturer = ups.Manufacturer,
                Model = ups.Model,
                Serial = ups.Serial,
                StatusText = "UNKNOWN",
                IsOnLine = false,
                IsOnBattery = false,
                IsLowBattery = false,
                NutConnected = false,
                NutError = "Nessuna misura disponibile: verificare che l'orchestrator sia attivo.",
                IsStale = true
            };
        }

        var flags = (UpsStatusFlags)latest.StatusFlags;

        // La freschezza è ricalcolata adesso: una misura scritta 10 minuti fa era valida allora,
        // non lo è ora. Il flag persistito non basta.
        var age = clock.UtcNow - latest.RecordedAt;
        var isStale = latest.IsStale || age > TimeSpan.FromSeconds(ups.StaleDataThresholdSeconds);

        return new UpsStatusDto
        {
            Id = ups.Id,
            Name = ups.Name,
            Manufacturer = ups.Manufacturer,
            Model = ups.Model,
            Serial = ups.Serial,
            StatusText = flags.ToNutString(),
            IsOnLine = flags.IsOnLine(),
            IsOnBattery = flags.IsOnBattery(),
            IsLowBattery = flags.IsLowBattery(),
            BatteryChargePercent = latest.BatteryChargePercent,
            BatteryRuntimeSeconds = latest.BatteryRuntimeSeconds,
            BatteryVoltage = latest.BatteryVoltage,
            InputVoltage = latest.InputVoltage,
            OutputVoltage = latest.OutputVoltage,
            InputFrequency = latest.InputFrequency,
            OutputFrequency = latest.OutputFrequency,
            LoadPercent = latest.LoadPercent,
            RealPowerWatts = latest.RealPowerWatts,
            PhaseData = ParsePhaseData(latest.PhaseDataJson),
            LastCommunicationAt = latest.RecordedAt,
            NutConnected = !flags.IsUnknown() && !isStale,
            NutError = isStale
                ? $"Ultimo dato ricevuto {age.TotalSeconds:F0} secondi fa: informazione obsoleta."
                : null,
            IsStale = isStale
        };
    }

    private static IReadOnlyDictionary<string, string> ParsePhaseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public async Task<InfrastructureStatusDto> GetInfrastructureStatusAsync(
        CancellationToken ct = default)
    {
        var counts = await db.ManagedDevices
            .AsNoTracking()
            .GroupBy(d => d.LastKnownState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(DeviceState state) =>
            counts.FirstOrDefault(c => c.State == state)?.Count ?? 0;

        return new InfrastructureStatusDto
        {
            TotalDevices = counts.Sum(c => c.Count),
            DevicesOn = CountOf(DeviceState.On),
            DevicesOff = CountOf(DeviceState.Off),
            DevicesUnreachable = CountOf(DeviceState.Unreachable),
            DevicesShuttingDown = CountOf(DeviceState.ShuttingDown),
            DevicesStarting = CountOf(DeviceState.Starting),
            DevicesWithErrors = CountOf(DeviceState.Error),
            DevicesUnknown = CountOf(DeviceState.Unknown)
        };
    }

    public async Task<AutomationStatusDto> GetAutomationStatusAsync(CancellationToken ct = default)
    {
        var modeRaw = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.ExecutionMode)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var adaptersRaw = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.LiveEnabledAdapters)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var heartbeats = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct);
        var orchestrator = heartbeats.FirstOrDefault(h => h.WorkerName == OrchestratorWorkerName);
        var agent = heartbeats.FirstOrDefault(h => h.WorkerName == SafetyAgentWorkerName);

        var activeConfig = await db.ConfigurationVersions
            .AsNoTracking()
            .Where(c => c.State == ConfigurationState.Published)
            .Select(c => c.Version)
            .FirstOrDefaultAsync(ct);

        // Ultimo blackout noto, per mostrare durata e stato corrente.
        var lastBlackout = await db.UpsEvents
            .AsNoTracking()
            .Where(e => e.EventType == UpsEventType.BlackoutStarted && e.BlackoutId != null)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(ct);

        var blackoutEnded = lastBlackout is not null && await db.UpsEvents
            .AsNoTracking()
            .AnyAsync(e => e.BlackoutId == lastBlackout.BlackoutId
                        && e.EventType == UpsEventType.PowerRestored, ct);

        var latestMeasurement = await db.UpsMeasurements
            .AsNoTracking()
            .OrderByDescending(m => m.RecordedAt)
            .FirstOrDefaultAsync(ct);

        return new AutomationStatusDto
        {
            ExecutionMode = Enum.TryParse<ExecutionMode>(modeRaw, true, out var mode)
                ? mode
                : ExecutionMode.DryRun,
            AutomationDisabled = killSwitch.IsAutomationDisabled(),
            LiveEnabledAdapters = string.IsNullOrWhiteSpace(adaptersRaw)
                ? []
                : [.. adaptersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            OrchestratorState = DescribeWorker(orchestrator, TimeSpan.FromSeconds(60)),
            OrchestratorLastHeartbeat = orchestrator?.LastSeenAt,
            SafetyAgentState = DescribeWorker(agent, TimeSpan.FromSeconds(120)),
            SafetyAgentLastHeartbeat = agent?.LastSeenAt,
            CurrentBlackoutId = blackoutEnded ? null : lastBlackout?.BlackoutId,
            BlackoutDurationSeconds = blackoutEnded || lastBlackout is null
                ? null
                : (int)(clock.UtcNow - lastBlackout.OccurredAt).TotalSeconds,
            RemainingRuntimeSeconds = latestMeasurement?.BatteryRuntimeSeconds,
            ActiveConfigVersion = activeConfig
        };
    }

    /// <summary>
    /// Descrive lo stato di un worker in base all'età del suo heartbeat. Un worker che non
    /// scrive da troppo tempo non è "attivo": va mostrato come non verificabile (§5.3).
    /// </summary>
    private string DescribeWorker(WorkerHeartbeat? heartbeat, TimeSpan tolerance)
    {
        if (heartbeat is null) return "Mai visto";

        var age = clock.UtcNow - heartbeat.LastSeenAt;
        if (age <= tolerance) return heartbeat.State;

        return $"Non risponde da {age.TotalSeconds:F0}s";
    }

    /// <summary>Invia lo stato corrente ai client connessi via SignalR (§5.3).</summary>
    public async Task BroadcastAsync(CancellationToken ct = default)
    {
        var automation = await GetAutomationStatusAsync(ct);
        var infrastructure = await GetInfrastructureStatusAsync(ct);
        var upsList = await GetUpsStatusAsync(ct);

        await hub.Clients.All.AutomationStatusChanged(automation);
        await hub.Clients.All.InfrastructureStatusChanged(infrastructure);

        foreach (var ups in upsList)
            await hub.Clients.All.UpsStatusChanged(ups);
    }
}
