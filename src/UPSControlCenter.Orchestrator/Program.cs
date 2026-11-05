using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Sequencing;
using UPSControlCenter.Adapters;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Services;
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

void ConfigureDbContext(DbContextOptionsBuilder o) => o
    .UseNpgsql(connectionString, npg => npg
        .MigrationsHistoryTable("__ef_migrations_history", UpsDbContext.Schema)
        // Resilienza alle disconnessioni: il database è in un container che può riavviarsi.
        .EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
    .UseSnakeCaseNamingConvention();

// Un solo punto di registrazione delle opzioni: AddDbContextFactory le registra come
// singleton. Registrare anche AddDbContext in aggiunta provocherebbe un conflitto di lifetime
// (DbContextOptions scoped vs singleton) rilevato solo all'avvio dell'applicazione, non in
// compilazione. UpsDbContext "diretto" (per Identity, i controller, ecc.) viene quindi creato
// a partire dalla factory, con lifetime scoped esplicito.
builder.Services.AddDbContextFactory<UpsDbContext>(ConfigureDbContext);
builder.Services.AddScoped<UpsDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<UpsDbContext>>().CreateDbContext());

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

// Esecuzione delle sequenze: stesso motore condiviso con Safety Agent e Simulatore (§2.1).
builder.Services.AddSingleton<SequenceExecutor>();
builder.Services.AddScoped<ISequenceRunStore, PostgresSequenceRunStore>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddSingleton<ShutdownExecutionCoordinator>();
builder.Services.AddSingleton<StartupExecutionCoordinator>();

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
