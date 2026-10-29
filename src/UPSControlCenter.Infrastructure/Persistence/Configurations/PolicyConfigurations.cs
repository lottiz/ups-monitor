using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class ShutdownPolicyConfiguration : IEntityTypeConfiguration<ShutdownPolicy>
{
    public void Configure(EntityTypeBuilder<ShutdownPolicy> b)
    {
        b.ToTable("shutdown_policies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.CriticalBatteryChargePercent).HasPrecision(5, 2);

        // Una sola policy attiva per volta, garantito dal database e non dal codice (§4.3).
        b.HasIndex(x => x.IsActive)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_shutdown_policies_single_active");

        b.HasOne(x => x.DefaultSequence)
            .WithMany()
            .HasForeignKey(x => x.DefaultSequenceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_shutdown_policies_max_blackout",
                "max_blackout_duration_seconds BETWEEN 30 AND 86400");
            t.HasCheckConstraint("ck_shutdown_policies_min_runtime",
                "min_remaining_runtime_seconds BETWEEN 60 AND 86400");
            t.HasCheckConstraint("ck_shutdown_policies_debounce",
                "debounce_samples BETWEEN 1 AND 60");
            t.HasCheckConstraint("ck_shutdown_policies_takeover",
                "takeover_grace_seconds BETWEEN 30 AND 3600");
            t.HasCheckConstraint("ck_shutdown_policies_critical_charge",
                "critical_battery_charge_percent IS NULL OR " +
                "critical_battery_charge_percent BETWEEN 0 AND 100");
        });

        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class ShutdownRuleConfiguration : IEntityTypeConfiguration<ShutdownRule>
{
    public void Configure(EntityTypeBuilder<ShutdownRule> b)
    {
        b.ToTable("shutdown_rules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.ConditionTreeJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.TimeWindowsJson).HasColumnType("jsonb");

        b.HasOne(x => x.ShutdownPolicy)
            .WithMany(x => x.Rules)
            .HasForeignKey(x => x.ShutdownPolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.TriggerSequence)
            .WithMany()
            .HasForeignKey(x => x.TriggerSequenceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.ShutdownPolicyId, x.Priority });
        b.HasIndex(x => new { x.ShutdownPolicyId, x.Name }).IsUnique();
    }
}

public sealed class StartupPolicyConfiguration : IEntityTypeConfiguration<StartupPolicy>
{
    public void Configure(EntityTypeBuilder<StartupPolicy> b)
    {
        b.ToTable("startup_policies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.MinBatteryChargePercent).HasPrecision(5, 2);

        b.HasIndex(x => x.IsActive)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_startup_policies_single_active");

        b.HasOne(x => x.DefaultSequence)
            .WithMany()
            .HasForeignKey(x => x.DefaultSequenceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_startup_policies_stable_power",
                "required_stable_power_seconds BETWEEN 30 AND 86400");
            // La stabilità non può essere valutata su una sola lettura (§12).
            t.HasCheckConstraint("ck_startup_policies_samples",
                "stability_samples BETWEEN 2 AND 1000");
            t.HasCheckConstraint("ck_startup_policies_charge",
                "min_battery_charge_percent BETWEEN 0 AND 100");
        });

        b.Property<uint>("xmin").IsRowVersion();
    }
}
