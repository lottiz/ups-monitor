using Microsoft.EntityFrameworkCore;
using Npgsql;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Tests.Persistence;

/// <summary>
/// Verifica che i vincoli di sicurezza esistano <b>nel database</b> e non solo nel codice.
/// <para>
/// È una distinzione sostanziale: l'idempotenza delle azioni e la mutua esclusione fra
/// Orchestrator e Safety Agent si basano su vincoli PostgreSQL (§4.5, R2). Se un indice
/// parziale fosse generato male dalle migrazioni, i test in memoria passerebbero comunque e il
/// difetto emergerebbe solo durante un blackout reale.
/// </para>
/// <para>
/// I test girano solo se è presente <c>UPS_TEST_CONNECTION</c>; altrimenti vengono saltati,
/// così la suite resta eseguibile senza un PostgreSQL a disposizione.
/// </para>
/// </summary>
public sealed class PostgresConstraintTests : IAsyncLifetime
{
    private const string ConnectionVariable = PostgresFactAttribute.ConnectionVariable;

    private readonly string? _baseConnectionString =
        Environment.GetEnvironmentVariable(ConnectionVariable);

    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private bool _available;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString)) return;

        // Database dedicato per ogni esecuzione: i test non devono interferire fra loro né
        // con un'eventuale istanza di sviluppo.
        _databaseName = $"upscc_ct_{Guid.NewGuid():N}"[..24];

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var adminConnection = builder.ConnectionString;
        builder.Database = _databaseName;
        _connectionString = builder.ConnectionString;

        try
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await cmd.ExecuteNonQueryAsync();

            await using var db = CreateContext();
            await db.Database.MigrateAsync();

            _available = true;
        }
        catch (NpgsqlException)
        {
            // PostgreSQL dichiarato ma non raggiungibile: i test vengono saltati.
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString!);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    private UpsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<UpsDbContext>()
            .UseNpgsql(_connectionString, npg => npg
                .MigrationsHistoryTable("__ef_migrations_history", UpsDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new UpsDbContext(options);
    }

    /// <summary>
    /// Fallisce in modo esplicito se PostgreSQL è dichiarato ma non raggiungibile: in quel caso
    /// il test non deve passare in silenzio, perché il vincolo non è stato verificato.
    /// </summary>
    private void RequireDatabase()
    {
        Assert.True(_available,
            $"{ConnectionVariable} è impostata ma il database non è raggiungibile o le " +
            "migrazioni non si sono applicate: il vincolo non è stato verificato.");
    }

    [PostgresFact]
    public async Task Le_migrazioni_si_applicano_su_PostgreSQL_reale()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
    }

    [PostgresFact]
    public async Task Il_vincolo_di_idempotenza_sulle_azioni_e_applicato_dal_database()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var run = new OrchestrationRun
        {
            BlackoutId = "riello:1000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "test",
            ExecutionMode = ExecutionMode.DryRun,
            State = RunState.Running,
            Owner = WorkerRole.Orchestrator,
            StartedAt = DateTimeOffset.UtcNow
        };
        db.OrchestrationRuns.Add(run);
        await db.SaveChangesAsync();

        var stepId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();

        db.OrchestrationActions.Add(new OrchestrationAction
        {
            OrchestrationRunId = run.Id,
            StepId = stepId,
            DeviceId = deviceId,
            Attempt = 1,
            ActionType = "ShutdownDevice",
            State = ActionState.Succeeded,
            StartedAt = DateTimeOffset.UtcNow,
            AdapterName = "DryRun",
            WasDryRun = true
        });
        await db.SaveChangesAsync();

        // Seconda azione con la stessa chiave logica: il database deve rifiutarla.
        // È il meccanismo che impedisce la doppia esecuzione (§4.5).
        await using var second = CreateContext();
        second.OrchestrationActions.Add(new OrchestrationAction
        {
            OrchestrationRunId = run.Id,
            StepId = stepId,
            DeviceId = deviceId,
            Attempt = 1,
            ActionType = "ShutdownDevice",
            State = ActionState.Succeeded,
            StartedAt = DateTimeOffset.UtcNow,
            AdapterName = "DryRun",
            WasDryRun = true
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Un_nuovo_tentativo_della_stessa_azione_e_consentito()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var run = new OrchestrationRun
        {
            BlackoutId = "riello:2000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "test",
            State = RunState.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        db.OrchestrationRuns.Add(run);

        var stepId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();

        // Attempt diversi: la chiave logica cambia, quindi il retry è legittimo.
        foreach (var attempt in new[] { 1, 2, 3 })
        {
            db.OrchestrationActions.Add(new OrchestrationAction
            {
                OrchestrationRunId = run.Id,
                StepId = stepId,
                DeviceId = deviceId,
                Attempt = attempt,
                ActionType = "ShutdownDevice",
                State = attempt < 3 ? ActionState.Failed : ActionState.Succeeded,
                StartedAt = DateTimeOffset.UtcNow,
                AdapterName = "DryRun",
                WasDryRun = true
            });
        }

        await db.SaveChangesAsync();

        var count = await db.OrchestrationActions.CountAsync(a => a.OrchestrationRunId == run.Id);
        Assert.Equal(3, count);
    }

    [PostgresFact]
    public async Task Non_possono_esistere_due_sequenze_attive_per_lo_stesso_blackout()
    {
        RequireDatabase();

        await using var db = CreateContext();

        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            BlackoutId = "riello:3000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "orchestrator",
            State = RunState.Running,
            Owner = WorkerRole.Orchestrator,
            StartedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Il Safety Agent tenta di avviare la stessa sequenza dopo un takeover: il vincolo
        // parziale lo impedisce, evitando lo split-brain (R2).
        await using var agent = CreateContext();
        agent.OrchestrationRuns.Add(new OrchestrationRun
        {
            BlackoutId = "riello:3000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "safety agent takeover",
            State = RunState.Pending,
            Owner = WorkerRole.SafetyAgent,
            StartedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => agent.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Una_sequenza_completata_non_blocca_un_nuovo_avvio()
    {
        RequireDatabase();

        await using var db = CreateContext();

        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            BlackoutId = "riello:4000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "primo tentativo",
            State = RunState.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // L'indice è parziale sugli stati attivi: una sequenza fallita non deve bloccare
        // per sempre la protezione di quel blackout.
        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            BlackoutId = "riello:4000",
            RunKind = RunKind.Shutdown,
            TriggerReason = "secondo tentativo",
            State = RunState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var count = await db.OrchestrationRuns.CountAsync(r => r.BlackoutId == "riello:4000");
        Assert.Equal(2, count);
    }

    [PostgresFact]
    public async Task Puo_esistere_una_sola_policy_di_spegnimento_attiva()
    {
        RequireDatabase();

        await using var db = CreateContext();

        db.ShutdownPolicies.Add(new ShutdownPolicy
        {
            Name = "Policy A",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Due policy attive renderebbero indefinito quale regola applicare durante un blackout.
        await using var second = CreateContext();
        second.ShutdownPolicies.Add(new ShutdownPolicy
        {
            Name = "Policy B",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Puo_esistere_una_sola_configurazione_pubblicata()
    {
        RequireDatabase();

        await using var db = CreateContext();

        db.ConfigurationVersions.Add(new ConfigurationVersion
        {
            Version = 1,
            PayloadJson = "{}",
            ChecksumSha256 = "a",
            Signature = [1],
            State = ConfigurationState.Published,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Due versioni pubblicate renderebbero ambigua la configurazione servita all'agent.
        await using var second = CreateContext();
        second.ConfigurationVersions.Add(new ConfigurationVersion
        {
            Version = 2,
            PayloadJson = "{}",
            ChecksumSha256 = "b",
            Signature = [2],
            State = ConfigurationState.Published,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task I_check_constraint_sulle_soglie_sono_applicati()
    {
        RequireDatabase();

        await using var db = CreateContext();

        // La soglia di obsolescenza deve superare l'intervallo di polling: con valori
        // invertiti ogni lettura risulterebbe obsoleta e il sistema non deciderebbe mai.
        db.UpsDevices.Add(new UpsDevice
        {
            Name = "UPS incoerente",
            NutHost = "127.0.0.1",
            NutPort = 3493,
            NutUpsName = "test",
            PollingIntervalSeconds = 60,
            StaleDataThresholdSeconds = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Un_dispositivo_non_puo_dipendere_da_se_stesso()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var device = TestData.Device("srv-solo");
        db.ManagedDevices.Add(device);
        await db.SaveChangesAsync();

        db.DeviceDependencies.Add(new DeviceDependency
        {
            DependentDeviceId = device.Id,
            DependsOnDeviceId = device.Id
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Le_metriche_non_disponibili_restano_NULL_dopo_il_salvataggio()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var ups = new UpsDevice
        {
            Name = "UPS con dati parziali",
            NutHost = "127.0.0.1",
            NutPort = 3493,
            NutUpsName = "parziale",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.UpsDevices.Add(ups);

        db.UpsMeasurements.Add(new UpsMeasurement
        {
            UpsDeviceId = ups.Id,
            RecordedAt = DateTimeOffset.UtcNow,
            StatusRaw = "OB",
            StatusFlags = 2,
            // Nessuna metrica disponibile: devono restare NULL anche dopo il round-trip.
            BatteryRuntimeSeconds = null,
            BatteryChargePercent = null,
            LoadPercent = null
        });

        await db.SaveChangesAsync();

        await using var reader = CreateContext();
        var stored = await reader.UpsMeasurements
            .AsNoTracking()
            .FirstAsync(m => m.UpsDeviceId == ups.Id);

        // Requisito §9.3 verificato end-to-end: il database non trasforma l'assenza in zero.
        Assert.Null(stored.BatteryRuntimeSeconds);
        Assert.Null(stored.BatteryChargePercent);
        Assert.Null(stored.LoadPercent);
    }

    [PostgresFact]
    public async Task Un_dispositivo_puo_avere_piu_sorgenti_di_alimentazione()
    {
        RequireDatabase();

        await using var db = CreateContext();

        var upsA = new UpsDevice
        {
            Name = "UPS A", NutHost = "10.0.0.8", NutPort = 3493, NutUpsName = "ups-a",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var upsB = new UpsDevice
        {
            Name = "UPS B", NutHost = "10.0.0.9", NutPort = 3493, NutUpsName = "ups-b",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.UpsDevices.AddRange(upsA, upsB);

        var device = TestData.Device("srv-doppia-alimentazione");
        device.PowerSources.Add(new DevicePowerSource { UpsDeviceId = upsA.Id, IsPrimary = true });
        device.PowerSources.Add(new DevicePowerSource { UpsDeviceId = upsB.Id, IsPrimary = false });
        db.ManagedDevices.Add(device);

        await db.SaveChangesAsync();

        await using var reader = CreateContext();
        var stored = await reader.ManagedDevices
            .AsNoTracking()
            .Include(d => d.PowerSources)
            .FirstAsync(d => d.Id == device.Id);

        // §6: la perdita di una sola sorgente non implica assenza di alimentazione.
        Assert.Equal(2, stored.PowerSources.Count);
        Assert.Single(stored.PowerSources, p => p.IsPrimary);
    }

    [PostgresFact]
    public async Task Lo_stesso_UPS_fisico_non_puo_essere_definito_due_volte()
    {
        RequireDatabase();

        await using var db = CreateContext();

        db.UpsDevices.Add(new UpsDevice
        {
            Name = "UPS primo", NutHost = "10.0.0.8", NutPort = 3493, NutUpsName = "riello",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Nome diverso ma stesso endpoint NUT: sarebbe lo stesso UPS contato due volte.
        await using var second = CreateContext();
        second.UpsDevices.Add(new UpsDevice
        {
            Name = "UPS duplicato", NutHost = "10.0.0.8", NutPort = 3493, NutUpsName = "riello",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task I_dati_iniziali_creano_il_sistema_in_modalita_DRY_RUN()
    {
        RequireDatabase();

        await using var db = CreateContext();
        await SeedData.SeedAsync(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var mode = await db.SystemSettings
            .AsNoTracking()
            .FirstAsync(s => s.Key == SystemSettingKeys.ExecutionMode);

        // Vincolo assoluto del progetto: il sistema nasce in DRY_RUN, senza adapter reali.
        Assert.Equal(nameof(ExecutionMode.DryRun), mode.Value);

        var adapters = await db.SystemSettings
            .AsNoTracking()
            .FirstAsync(s => s.Key == SystemSettingKeys.LiveEnabledAdapters);

        Assert.True(string.IsNullOrEmpty(adapters.Value));

        var policy = await db.ShutdownPolicies
            .AsNoTracking()
            .Include(p => p.Rules)
            .FirstAsync(p => p.IsActive);

        Assert.Equal(ExecutionMode.DryRun, policy.ExecutionMode);
        Assert.Equal(1800, policy.MaxBlackoutDurationSeconds);
        Assert.Equal(1800, policy.MinRemainingRuntimeSeconds);
        Assert.Equal(2, policy.Rules.Count);
    }
}
