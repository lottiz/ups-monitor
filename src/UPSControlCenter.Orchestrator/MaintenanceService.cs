using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Orchestrator;

/// <summary>
/// Manutenzione periodica: applica la retention allo storico per evitare che il database cresca
/// senza limiti sul mini PC (§3.1, R13).
/// </summary>
public sealed class MaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrchestratorOptions> options,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private readonly OrchestratorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Attesa iniziale: all'avvio la priorità è il monitoraggio, non la pulizia.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manutenzione non completata. Nuovo tentativo al ciclo successivo.");
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.MaintenanceIntervalMinutes), stoppingToken);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        var measurementDays = await GetRetentionAsync(db, SystemSettingKeys.MeasurementRetentionDays,
            _options.DefaultMeasurementRetentionDays, ct);
        var eventDays = await GetRetentionAsync(db, SystemSettingKeys.EventRetentionDays, 365, ct);

        var measurementCutoff = DateTimeOffset.UtcNow.AddDays(-measurementDays);
        var eventCutoff = DateTimeOffset.UtcNow.AddDays(-eventDays);

        // ExecuteDelete: cancellazione lato database, senza materializzare milioni di righe.
        var deletedMeasurements = await db.UpsMeasurements
            .Where(m => m.RecordedAt < measurementCutoff)
            .ExecuteDeleteAsync(ct);

        var deletedEvents = await db.UpsEvents
            .Where(e => e.OccurredAt < eventCutoff)
            .ExecuteDeleteAsync(ct);

        // L'audit non viene mai cancellato da qui: la sua retention è più lunga e la rimozione
        // richiede una decisione amministrativa esplicita (§4.4).
        if (deletedMeasurements > 0 || deletedEvents > 0)
        {
            logger.LogInformation(
                "Manutenzione completata: {Measurements} misure e {Events} eventi rimossi " +
                "(retention {MeasurementDays}/{EventDays} giorni).",
                deletedMeasurements, deletedEvents, measurementDays, eventDays);
        }
    }

    private static async Task<int> GetRetentionAsync(UpsDbContext db, string key, int fallback,
        CancellationToken ct)
    {
        var raw = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // Un valore non valido non deve causare cancellazioni impreviste: si usa il ripiego.
        return int.TryParse(raw, out var days) && days > 0 ? days : fallback;
    }
}
