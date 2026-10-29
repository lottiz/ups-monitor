using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence.Configurations;

public sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> b)
    {
        b.ToTable("notification_channels");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.ConfigJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SecretCiphertext).HasColumnType("bytea");

        b.ToTable(t => t.HasCheckConstraint("ck_notification_channels_cooldown",
            "cooldown_seconds BETWEEN 0 AND 86400"));
    }
}

public sealed class NotificationEventConfiguration : IEntityTypeConfiguration<NotificationEvent>
{
    public void Configure(EntityTypeBuilder<NotificationEvent> b)
    {
        b.ToTable("notification_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.DedupKey).HasMaxLength(256).IsRequired();
        b.Property(x => x.Error).HasMaxLength(2048);
        b.Property(x => x.PayloadJson).HasColumnType("jsonb");

        b.HasOne(x => x.NotificationChannel)
            .WithMany()
            .HasForeignKey(x => x.NotificationChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Base della deduplicazione: la finestra di cooldown è applicata in query (§17).
        b.HasIndex(x => new { x.NotificationChannelId, x.DedupKey, x.CreatedAt });
        b.HasIndex(x => x.State);
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.UserName).HasMaxLength(256);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.Action).HasMaxLength(128).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(128).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(128);
        b.Property(x => x.BeforeJson).HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnType("jsonb");
        b.Property(x => x.Outcome).HasMaxLength(64).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(2048);

        b.HasIndex(x => x.OccurredAt).IsDescending();
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.HasIndex(x => x.UserId);
    }
}

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ToTable("system_settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Value).HasMaxLength(4096);
        b.Property(x => x.ValueType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Description).HasMaxLength(512);
        b.Property(x => x.UpdatedBy).HasMaxLength(256);
    }
}
