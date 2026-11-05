using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Adapters.Live;

/// <summary>
/// Sincronizza periodicamente <see cref="ExecutionModeOptions"/> con <c>SystemSettings</c>.
/// <para>
/// Senza questo servizio, l'abilitazione di un adapter dal portale (§8.2) scriverebbe nel
/// database ma non avrebbe alcun effetto sul processo in esecuzione finché non viene riavviato:
/// <see cref="ExecutionModeOptions"/> è un singleton costruito una sola volta all'avvio dalla
/// sola configurazione statica. Questo servizio chiude il cerchio, mantenendo però
/// <see cref="ExecutionModeOptions.DeploymentAllowsLive"/> invariato: quel valore viene solo
/// dalla variabile d'ambiente del deployment, mai dal database (R18).
/// </para>
/// </summary>
public sealed class ExecutionModeSyncService(
    IServiceScopeFactory scopeFactory,
    ExecutionModeOptions options,
    ILogger<ExecutionModeSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Il database può essere temporaneamente irraggiungibile: non è un motivo per
                // smettere di provare, e nel frattempo le impostazioni correnti restano valide
                // (l'ultima lettura riuscita, o il valore iniziale conservativo DryRun).
                logger.LogWarning(ex, "Sincronizzazione della modalità di esecuzione non riuscita.");
            }

            await Task.Delay(SyncInterval, stoppingToken);
        }
    }

    // Internal (non private) solo per essere esercitato direttamente dai test, evitando di
    // dipendere dal timer di BackgroundService (10s) per verificare la logica di sincronizzazione.
    internal async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();

        var modeRaw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.ExecutionMode)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // Un valore non riconosciuto non deve mai abilitare LIVE per errore: ricade su DryRun.
        var newMode = Enum.TryParse<ExecutionMode>(modeRaw, true, out var parsedMode)
            ? parsedMode
            : ExecutionMode.DryRun;

        var adaptersRaw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.LiveEnabledAdapters)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var newAdapters = string.IsNullOrWhiteSpace(adaptersRaw)
            ? []
            : adaptersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        var changed = options.Mode != newMode
            || !options.LiveEnabledAdapters.SequenceEqual(newAdapters, StringComparer.OrdinalIgnoreCase);

        if (changed)
        {
            logger.LogWarning(
                "Modalità di esecuzione aggiornata dal database: {Mode} (autorizzazione di " +
                "deployment: {Allowed}), adapter abilitati: [{Adapters}].",
                newMode, options.DeploymentAllowsLive, string.Join(", ", newAdapters));
        }

        options.Mode = newMode;
        options.LiveEnabledAdapters = newAdapters;
    }
}
