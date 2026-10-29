using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Web.Services;

/// <summary>
/// Verifica PostgreSQL e lo stato delle migrazioni. Un database raggiungibile ma con migrazioni
/// mancanti è un guasto: l'applicazione fallirebbe alla prima query (§6.4).
/// </summary>
public sealed class DatabaseHealthCheck(UpsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL non raggiungibile.");

            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            var list = pending.ToList();

            return list.Count > 0
                ? HealthCheckResult.Unhealthy(
                    $"Migrazioni non applicate: {string.Join(", ", list)}. " +
                    "Eseguire 'docker compose run --rm ups-web migrate'.")
                : HealthCheckResult.Healthy("PostgreSQL raggiungibile, schema aggiornato.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Errore nella verifica del database.", ex);
        }
    }
}

/// <summary>
/// Verifica che l'orchestrator stia scrivendo heartbeat.
/// <para>
/// Restituisce <see cref="HealthStatus.Degraded"/> e non <c>Unhealthy</c>: il portale resta
/// utilizzabile e il Safety Agent garantisce la protezione, quindi far riavviare il container web
/// non risolverebbe nulla e peggiorerebbe la visibilità sul problema.
/// </para>
/// </summary>
public sealed class OrchestratorHealthCheck(UpsDbContext db, IClock clock) : IHealthCheck
{
    private const string WorkerName = "ups-orchestrator";
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(90);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var heartbeat = await db.WorkerHeartbeats
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.WorkerName == WorkerName, cancellationToken);

            if (heartbeat is null)
                return HealthCheckResult.Degraded(
                    "Nessun heartbeat dell'orchestrator: il monitoraggio NUT potrebbe non essere attivo.");

            var age = clock.UtcNow - heartbeat.LastSeenAt;

            return age <= Tolerance
                ? HealthCheckResult.Healthy($"Orchestrator attivo (ultimo heartbeat {age.TotalSeconds:F0}s).")
                : HealthCheckResult.Degraded(
                    $"Heartbeat dell'orchestrator vecchio di {age.TotalSeconds:F0}s. " +
                    "Il Safety Agent potrebbe assumere il controllo.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Impossibile verificare l'orchestrator.", ex);
        }
    }
}

/// <summary>
/// Verifica che il sistema sia nello stato di sicurezza atteso. Segnala come degradata la
/// modalità LIVE: non è un guasto, ma deve essere immediatamente visibile (§15).
/// </summary>
public sealed class ExecutionModeHealthCheck(UpsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var raw = await db.SystemSettings
                .AsNoTracking()
                .Where(s => s.Key == SystemSettingKeys.ExecutionMode)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken);

            var mode = Enum.TryParse<ExecutionMode>(raw, true, out var m) ? m : ExecutionMode.DryRun;

            return mode == ExecutionMode.DryRun
                ? HealthCheckResult.Healthy("Modalità DRY_RUN: nessuna operazione reale possibile.")
                : HealthCheckResult.Degraded(
                    "Modalità LIVE attiva: le operazioni reali sono possibili sugli adapter abilitati.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Impossibile determinare la modalità di esecuzione.", ex);
        }
    }
}
