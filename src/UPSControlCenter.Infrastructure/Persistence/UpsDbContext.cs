using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Infrastructure.Persistence;

/// <summary>
/// Contesto principale su PostgreSQL. Include Identity per evitare un secondo contesto e
/// mantenere le transazioni coerenti fra audit e dati applicativi (§4).
/// </summary>
public class UpsDbContext(DbContextOptions<UpsDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public const string Schema = "ups";

    public DbSet<UpsDevice> UpsDevices => Set<UpsDevice>();
    public DbSet<UpsMeasurement> UpsMeasurements => Set<UpsMeasurement>();
    public DbSet<UpsEvent> UpsEvents => Set<UpsEvent>();
    public DbSet<ManagedDevice> ManagedDevices => Set<ManagedDevice>();
    public DbSet<DevicePowerSource> DevicePowerSources => Set<DevicePowerSource>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<DeviceDependency> DeviceDependencies => Set<DeviceDependency>();
    public DbSet<ConnectionProfile> ConnectionProfiles => Set<ConnectionProfile>();
    public DbSet<ShutdownPolicy> ShutdownPolicies => Set<ShutdownPolicy>();
    public DbSet<ShutdownRule> ShutdownRules => Set<ShutdownRule>();
    public DbSet<ShutdownSequence> ShutdownSequences => Set<ShutdownSequence>();
    public DbSet<ShutdownStep> ShutdownSteps => Set<ShutdownStep>();
    public DbSet<StartupPolicy> StartupPolicies => Set<StartupPolicy>();
    public DbSet<StartupSequence> StartupSequences => Set<StartupSequence>();
    public DbSet<StartupStep> StartupSteps => Set<StartupStep>();
    public DbSet<OrchestrationRun> OrchestrationRuns => Set<OrchestrationRun>();
    public DbSet<OrchestrationAction> OrchestrationActions => Set<OrchestrationAction>();
    public DbSet<SimulationRun> SimulationRuns => Set<SimulationRun>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<ConfigurationVersion> ConfigurationVersions => Set<ConfigurationVersion>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(UpsDbContext).Assembly);
    }
}
