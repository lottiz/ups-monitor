using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class OrchestrationRunConfiguration : IEntityTypeConfiguration<OrchestrationRun>
{
    public void Configure(EntityTypeBuilder<OrchestrationRun> b)
    {
        b.ToTable("orchestration_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.BlackoutId).HasMaxLength(128);
        b.Property(x => x.TriggerReason).HasMaxLength(1024).IsRequired();
        b.Property(x => x.RuleSnapshotJson).HasColumnType("jsonb");

        // Nessuna seconda sequenza attiva per lo stesso blackout: il vincolo è nel database, così
        // Orchestrator e Safety Agent non possono duplicare l'esecuzione nemmeno in caso di
        // split-brain (§4.4, R2).
        b.HasIndex(x => new { x.BlackoutId, x.RunKind })
            .IsUnique()
            .HasFilter($"state IN ({(int)RunState.Pending}, {(int)RunState.Running}) " +
                       "AND blackout_id IS NOT NULL")
            .HasDatabaseName("ux_orchestration_runs_active_per_blackout");

        b.HasIndex(x => x.StartedAt).IsDescending();
        b.HasIndex(x => x.State);
    }
}

public sealed class OrchestrationActionConfiguration : IEntityTypeConfiguration<OrchestrationAction>
{
    public void Configure(EntityTypeBuilder<OrchestrationAction> b)
    {
        b.ToTable("orchestration_actions");
        b.HasKey(x => x.Id);
        b.Property(x => x.ActionType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResultCode).HasMaxLength(64);
        b.Property(x => x.Message).HasMaxLength(2048);
        b.Property(x => x.AdapterName).HasMaxLength(64).IsRequired();

        b.HasOne(x => x.OrchestrationRun)
            .WithMany(x => x.Actions)
            .HasForeignKey(x => x.OrchestrationRunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotenza garantita dal database: una seconda esecuzione della stessa azione logica
        // viola il vincolo e viene scartata dal chiamante (§4.5, §13).
        b.HasIndex(x => new { x.OrchestrationRunId, x.StepId, x.DeviceId, x.Attempt })
            .IsUnique()
            .HasDatabaseName("ux_orchestration_actions_logical");

        b.ToTable(t => t.HasCheckConstraint("ck_orchestration_actions_attempt",
            "attempt BETWEEN 1 AND 10"));
    }
}

public sealed class SimulationRunConfiguration : IEntityTypeConfiguration<SimulationRun>
{
    public void Configure(EntityTypeBuilder<SimulationRun> b)
    {
        b.ToTable("simulation_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.ScenarioJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.TimelineJson).HasColumnType("jsonb");
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.Summary).HasMaxLength(2048);
        b.HasIndex(x => x.StartedAt).IsDescending();
    }
}

public sealed class WorkerHeartbeatConfiguration : IEntityTypeConfiguration<WorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<WorkerHeartbeat> b)
    {
        b.ToTable("worker_heartbeats");
        b.HasKey(x => x.WorkerName);
        b.Property(x => x.WorkerName).HasMaxLength(64);
        b.Property(x => x.InstanceId).HasMaxLength(128).IsRequired();
        b.Property(x => x.State).HasMaxLength(64).IsRequired();
        b.Property(x => x.DetailsJson).HasColumnType("jsonb");
    }
}

public sealed class ConfigurationVersionConfiguration : IEntityTypeConfiguration<ConfigurationVersion>
{
    public void Configure(EntityTypeBuilder<ConfigurationVersion> b)
    {
        b.ToTable("configuration_versions");
        b.HasKey(x => x.Id);
        b.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ChecksumSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.Signature).HasColumnType("bytea");
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.ActivatedByAgent).HasMaxLength(128);
        b.Property(x => x.ValidationErrorsJson).HasColumnType("jsonb");

        b.HasIndex(x => x.Version).IsUnique();

        // Una sola versione pubblicata: impedisce che il Safety Agent riceva configurazioni
        // ambigue (§4.4).
        b.HasIndex(x => x.State)
            .IsUnique()
            .HasFilter($"state = {(int)ConfigurationState.Published}")
            .HasDatabaseName("ux_configuration_versions_single_published");

        b.ToTable(t => t.HasCheckConstraint("ck_configuration_versions_version", "version > 0"));
    }
}
