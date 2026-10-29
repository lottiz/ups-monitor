using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class ManagedDeviceConfiguration : IEntityTypeConfiguration<ManagedDevice>
{
    public void Configure(EntityTypeBuilder<ManagedDevice> b)
    {
        b.ToTable("managed_devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.Hostname).HasMaxLength(255).IsRequired();
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.OperatingSystem).HasMaxLength(128);
        b.Property(x => x.MacAddress).HasMaxLength(17);
        b.Property(x => x.StatusCheckTarget).HasMaxLength(255);
        b.Property(x => x.Notes).HasMaxLength(2048);

        b.HasOne(x => x.DeviceGroup)
            .WithMany(x => x.Devices)
            .HasForeignKey(x => x.DeviceGroupId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.ConnectionProfile)
            .WithMany(x => x.Devices)
            .HasForeignKey(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_managed_devices_priority", "priority BETWEEN 1 AND 1000");
            t.HasCheckConstraint("ck_managed_devices_shutdown_timeout",
                "shutdown_timeout_seconds BETWEEN 10 AND 3600");
            t.HasCheckConstraint("ck_managed_devices_startup_timeout",
                "startup_timeout_seconds BETWEEN 10 AND 7200");
            t.HasCheckConstraint("ck_managed_devices_port",
                "management_port IS NULL OR management_port BETWEEN 1 AND 65535");
        });

        b.HasIndex(x => x.Priority);
        b.HasIndex(x => x.IsEnabled);
        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class DevicePowerSourceConfiguration : IEntityTypeConfiguration<DevicePowerSource>
{
    public void Configure(EntityTypeBuilder<DevicePowerSource> b)
    {
        b.ToTable("device_power_sources");

        // Chiave composta: un dispositivo può avere più sorgenti, ciascuna una sola volta (§6).
        b.HasKey(x => new { x.ManagedDeviceId, x.UpsDeviceId });

        b.HasOne(x => x.ManagedDevice)
            .WithMany(x => x.PowerSources)
            .HasForeignKey(x => x.ManagedDeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.UpsDevice)
            .WithMany(x => x.PowerSources)
            .HasForeignKey(x => x.UpsDeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.UpsDeviceId);
    }
}

public sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
{
    public void Configure(EntityTypeBuilder<DeviceGroup> b)
    {
        b.ToTable("device_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
    }
}

public sealed class DeviceDependencyConfiguration : IEntityTypeConfiguration<DeviceDependency>
{
    public void Configure(EntityTypeBuilder<DeviceDependency> b)
    {
        b.ToTable("device_dependencies");
        b.HasKey(x => x.Id);

        b.HasOne(x => x.DependentDevice)
            .WithMany(x => x.Dependencies)
            .HasForeignKey(x => x.DependentDeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.DependsOnDevice)
            .WithMany(x => x.Dependents)
            .HasForeignKey(x => x.DependsOnDeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.DependentDeviceId, x.DependsOnDeviceId }).IsUnique();

        // L'autodipendenza è impedita dal database; i cicli più lunghi richiedono l'analisi del
        // grafo e sono rifiutati in validazione (§10).
        b.ToTable(t => t.HasCheckConstraint("ck_device_dependencies_no_self",
            "dependent_device_id <> depends_on_device_id"));
    }
}

public sealed class ConnectionProfileConfiguration : IEntityTypeConfiguration<ConnectionProfile>
{
    public void Configure(EntityTypeBuilder<ConnectionProfile> b)
    {
        b.ToTable("connection_profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.Username).HasMaxLength(255).IsRequired();
        b.Property(x => x.SecretKeyId).HasMaxLength(128);
        b.Property(x => x.Fingerprint).HasMaxLength(255);

        // Il segreto è presente solo cifrato: non esiste alcuna colonna in chiaro (§8.4).
        b.Property(x => x.SecretCiphertext).HasColumnType("bytea");

        b.Property<uint>("xmin").IsRowVersion();
    }
}
