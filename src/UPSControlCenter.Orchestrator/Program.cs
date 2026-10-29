using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Infrastructure.Adapters;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Nut;
using UPSControlCenter.Orchestrator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<OrchestratorOptions>(
    builder.Configuration.GetSection(OrchestratorOptions.SectionName));
builder.Services.Configure<NutClientOptions>(
    builder.Configuration.GetSection(NutClientOptions.SectionName));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "Orchestrator")
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

builder.Services.AddSerilog(Log.Logger);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Stringa di connessione «Postgres» non configurata. " +
        "Impostare ConnectionStrings__Postgres nell'ambiente.");

builder.Services.AddDbContext<UpsDbContext>(o => o
    .UseNpgsql(connectionString, npg => npg
        .MigrationsHistoryTable("__ef_migrations_history", UpsDbContext.Schema)
        // Resilienza alle disconnessioni: il database è in un container che può riavviarsi.
        .EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<INutClient, NutTcpClient>();

// La modalità effettiva nasce DRY_RUN e diventa LIVE solo con l'autorizzazione di deployment
// UPS_ALLOW_LIVE: così un database ripristinato da un altro ambiente non può attivare azioni reali.
var executionOptions = new ExecutionModeOptions
{
    Mode = Enum.TryParse<UPSControlCenter.Core.Enums.ExecutionMode>(
        builder.Configuration["Execution:Mode"], true, out var mode)
        ? mode
        : UPSControlCenter.Core.Enums.ExecutionMode.DryRun,
    DeploymentAllowsLive = builder.Configuration["UPS_ALLOW_LIVE"] == "1"
                           || Environment.GetEnvironmentVariable("UPS_ALLOW_LIVE") == "1"
};
builder.Services.AddDeviceAdapters(executionOptions);

builder.Services.AddHostedService<NutPollingService>();
builder.Services.AddHostedService<MaintenanceService>();

var host = builder.Build();

Log.Information(
    "Orchestrator configurato in modalità effettiva {Mode} (richiesta: {Requested}, " +
    "autorizzazione deployment: {Allowed}).",
    executionOptions.EffectiveMode, executionOptions.Mode, executionOptions.DeploymentAllowsLive);

try
{
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "L'orchestrator si è arrestato per un errore non gestito.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
