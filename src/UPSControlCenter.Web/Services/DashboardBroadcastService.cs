namespace UPSControlCenter.Web.Services;

/// <summary>
/// Diffonde periodicamente lo stato ai client SignalR connessi (§5.3).
/// <para>
/// Un push periodico è preferibile a una notifica guidata dagli eventi: la dashboard deve
/// mostrare anche l'<b>invecchiamento</b> dei dati, e senza nuovi eventi (orchestrator fermo)
/// un modello puramente event-driven lascerebbe la pagina congelata su valori vecchi che
/// sembrano attuali.
/// </para>
/// </summary>
public sealed class DashboardBroadcastService(
    IServiceScopeFactory scopeFactory,
    ILogger<DashboardBroadcastService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var state = scope.ServiceProvider.GetRequiredService<DashboardStateService>();
                await state.BroadcastAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un errore di diffusione non deve fermare il servizio: al ciclo successivo
                // il database potrebbe essere di nuovo raggiungibile.
                logger.LogWarning(ex, "Diffusione dello stato non riuscita.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
