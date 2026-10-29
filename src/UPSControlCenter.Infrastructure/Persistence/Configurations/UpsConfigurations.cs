using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class UpsDeviceConfiguration : IEntityTypeConfiguration<UpsDevice>
{
    public void Configure(EntityTypeBuilder<UpsDevice> b)
    {
        b.ToTable("ups_devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.NutHost).HasMaxLength(255).IsRequired();
        b.Property(x => x.NutUpsName).HasMaxLength(128).IsRequired();
        b.Property(x => x.Manufacturer).HasMaxLength(128);
        b.Property(x => x.Model).HasMaxLength(128);
        b.Property(x => x.Serial).HasMaxLength(128);
        b.Property(x => x.FirmwareVersion).HasMaxLength(64);

        // Impedisce di definire due volte lo stesso UPS fisico (§4.1).
        b.HasIndex(x => new { x.NutHost, x.NutPort, x.NutUpsName }).IsUnique();

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_ups_devices_port", "nut_port BETWEEN 1 AND 65535");
            t.HasCheckConstraint("ck_ups_devices_polling", "polling_interval_seconds BETWEEN 2 AND 300");
            t.HasCheckConstraint("ck_ups_devices_timeout", "communication_timeout_seconds BETWEEN 5 AND 600");
            // La soglia di obsolescenza deve superare il polling, altrimenti ogni dato è obsoleto.
            t.HasCheckConstraint("ck_ups_devices_stale",
                "stale_data_threshold_seconds > polling_interval_seconds");
        });

        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class UpsMeasurementConfiguration : IEntityTypeConfiguration<UpsMeasurement>
{
    public void Configure(EntityTypeBuilder<UpsMeasurement> b)
    {
        b.ToTable("ups_measurements");
        b.HasKey(x => x.Id);
        b.Property(x => x.StatusRaw).HasMaxLength(128);
        b.Property(x => x.BatteryChargePercent).HasPrecision(5, 2);
        b.Property(x => x.LoadPercent).HasPrecision(5, 2);
        b.Property(x => x.BatteryVoltage).HasPrecision(8, 2);
        b.Property(x => x.InputVoltage).HasPrecision(8, 2);
        b.Property(x => x.OutputVoltage).HasPrecision(8, 2);
        b.Property(x => x.InputFrequency).HasPrecision(6, 2);
        b.Property(x => x.OutputFrequency).HasPrecision(6, 2);
        b.Property(x => x.RealPowerWatts).HasPrecision(10, 2);
        b.Property(x => x.PhaseDataJson).HasColumnType("jsonb");

        b.HasOne(x => x.UpsDevice)
            .WithMany(x => x.Measurements)
            .HasForeignKey(x => x.UpsDeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.UpsDeviceId, x.RecordedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ups_measurements_device_recorded");

        // BRIN: indice compatto ed efficiente sulle serie temporali append-only (§4.1).
        b.HasIndex(x => x.RecordedAt)
            .HasDatabaseName("ix_ups_measurements_recorded_brin")
            .HasMethod("brin");
    }
}

public sealed class UpsEventConfiguration : IEntityTypeConfiguration<UpsEvent>
{
    public void Configure(EntityTypeBuilder<UpsEvent> b)
    {
        b.ToTable("ups_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.BlackoutId).HasMaxLength(128);
        b.Property(x => x.PreviousStatus).HasMaxLength(128);
        b.Property(x => x.NewStatus).HasMaxLength(128);
        b.Property(x => x.DetailsJson).HasColumnType("jsonb");

        b.HasOne(x => x.UpsDevice)
            .WithMany(x => x.Events)
            .HasForeignKey(x => x.UpsDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => x.OccurredAt).IsDescending();
        b.HasIndex(x => x.BlackoutId);
        b.HasIndex(x => new { x.EventType, x.OccurredAt });
    }
}
