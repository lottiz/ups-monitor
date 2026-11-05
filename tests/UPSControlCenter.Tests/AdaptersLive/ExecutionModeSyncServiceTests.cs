using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters;
using UPSControlCenter.Adapters.Live;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Verifica <see cref="ExecutionModeSyncService"/> invocando direttamente
/// <see cref="ExecutionModeSyncService.SyncOnceAsync"/>, senza dipendere dal timer di
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> (10s, troppo lento per un test
/// unitario): la logica di sincronizzazione è ciò che si vuole esercitare, non lo scheduling.
/// </summary>
public class ExecutionModeSyncServiceTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ExecutionModeSyncServiceTests()
    {
        // Il nome va generato UNA SOLA VOLTA qui, non dentro la lambda: quella viene rieseguita a
        // ogni creazione di contesto, e Guid.NewGuid() lì dentro produrrebbe un database InMemory
        // diverso per ogni scope, isolando ogni DbContext dagli altri invece di condividerli.
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<UpsDbContext>(o => o.UseInMemoryDatabase(databaseName));
        _provider = services.BuildServiceProvider();
    }

    private async Task SeedAsync(string? executionMode, string? liveEnabledAdapters)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
        if (executionMode is not null)
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.ExecutionMode, Value = executionMode,
            });
        if (liveEnabledAdapters is not null)
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.LiveEnabledAdapters, Value = liveEnabledAdapters,
            });
        await db.SaveChangesAsync();
    }

    private ExecutionModeSyncService BuildService(ExecutionModeOptions options) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<ExecutionModeSyncService>.Instance);

    [Fact]
    public async Task Sincronizza_la_modalita_Live_dal_database_senza_toccare_DeploymentAllowsLive()
    {
        await SeedAsync("Live", "Ssh,Redfish");
        var options = new ExecutionModeOptions { DeploymentAllowsLive = true };
        var service = BuildService(options);

        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(ExecutionMode.Live, options.Mode);
        Assert.Equal(["Ssh", "Redfish"], options.LiveEnabledAdapters);
        Assert.True(options.DeploymentAllowsLive);
    }

    [Fact]
    public async Task Valore_non_riconosciuto_nel_database_ricade_su_DryRun_per_sicurezza()
    {
        await SeedAsync("ValoreCorrottoOIncomprensibile", null);
        var options = new ExecutionModeOptions { Mode = ExecutionMode.Live };
        var service = BuildService(options);

        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(ExecutionMode.DryRun, options.Mode);
    }

    [Fact]
    public async Task Nessuna_riga_nel_database_ricade_su_DryRun_e_nessun_adapter_abilitato()
    {
        var options = new ExecutionModeOptions
        {
            Mode = ExecutionMode.Live, LiveEnabledAdapters = ["Ssh"],
        };
        var service = BuildService(options);

        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(ExecutionMode.DryRun, options.Mode);
        Assert.Empty(options.LiveEnabledAdapters);
    }

    [Fact]
    public async Task Lista_adapter_vuota_o_con_spazi_in_piu_viene_interpretata_correttamente()
    {
        await SeedAsync("Live", " Ssh , QnapSsh ,, ");
        var options = new ExecutionModeOptions { DeploymentAllowsLive = true };
        var service = BuildService(options);

        await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(["Ssh", "QnapSsh"], options.LiveEnabledAdapters);
    }

    [Fact]
    public async Task Una_modifica_successiva_nel_database_si_riflette_alla_sincronizzazione_successiva()
    {
        await SeedAsync("DryRun", "");
        var options = new ExecutionModeOptions { DeploymentAllowsLive = true };
        var service = BuildService(options);

        await service.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(ExecutionMode.DryRun, options.Mode);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
            var setting = await db.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.ExecutionMode);
            setting.Value = "Live";
            await db.SaveChangesAsync();
        }

        await service.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(ExecutionMode.Live, options.Mode);
    }

    public ValueTask DisposeAsync()
    {
        _provider.Dispose();
        return ValueTask.CompletedTask;
    }
}
