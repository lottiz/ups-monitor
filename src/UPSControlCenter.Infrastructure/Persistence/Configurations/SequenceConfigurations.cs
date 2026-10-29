using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class ShutdownSequenceConfiguration : IEntityTypeConfiguration<ShutdownSequence>
{
    public void Configure(EntityTypeBuilder<ShutdownSequence> b)
    {
        b.ToTable("shutdown_sequences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class ShutdownStepConfiguration : IEntityTypeConfiguration<ShutdownStep>
{
    public void Configure(EntityTypeBuilder<ShutdownStep> b)
    {
        b.ToTable("shutdown_steps");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConditionJson).HasColumnType("jsonb");

        // uuid[] nativo di PostgreSQL: evita una tabella di giunzione per un elenco di esclusioni.
        b.Property(x => x.ExcludedDeviceIds).HasColumnType("uuid[]");

        b.HasOne(x => x.ShutdownSequence)
            .WithMany(x => x.Steps)
            .HasForeignKey(x => x.ShutdownSequenceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.TargetDevice)
            .WithMany()
            .HasForeignKey(x => x.TargetDeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.TargetGroup)
            .WithMany()
            .HasForeignKey(x => x.TargetGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // L'ordine degli step deve essere univoco: due step con lo stesso indice renderebbero
        // la sequenza non deterministica (§10).
        b.HasIndex(x => new { x.ShutdownSequenceId, x.OrderIndex }).IsUnique();

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_shutdown_steps_retries", "max_retries BETWEEN 0 AND 5");
            t.HasCheckConstraint("ck_shutdown_steps_timeout", "timeout_seconds BETWEEN 5 AND 7200");
            t.HasCheckConstraint("ck_shutdown_steps_delay", "delay_before_seconds BETWEEN 0 AND 3600");
        });
    }
}

public sealed class StartupSequenceConfiguration : IEntityTypeConfiguration<StartupSequence>
{
    public void Configure(EntityTypeBuilder<StartupSequence> b)
    {
        b.ToTable("startup_sequences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class StartupStepConfiguration : IEntityTypeConfiguration<StartupStep>
{
    public void Configure(EntityTypeBuilder<StartupStep> b)
    {
        b.ToTable("startup_steps");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConditionJson).HasColumnType("jsonb");
        b.Property(x => x.ExcludedDeviceIds).HasColumnType("uuid[]");

        b.HasOne(x => x.StartupSequence)
            .WithMany(x => x.Steps)
            .HasForeignKey(x => x.StartupSequenceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.TargetDevice)
            .WithMany()
            .HasForeignKey(x => x.TargetDeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.TargetGroup)
            .WithMany()
            .HasForeignKey(x => x.TargetGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.StartupSequenceId, x.OrderIndex }).IsUnique();

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_startup_steps_retries", "max_retries BETWEEN 0 AND 5");
            t.HasCheckConstraint("ck_startup_steps_timeout", "timeout_seconds BETWEEN 5 AND 7200");
        });
    }
}
