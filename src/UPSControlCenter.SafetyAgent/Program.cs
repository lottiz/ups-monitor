using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Formatting.Compact;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Infrastructure.Sqlite;
using UPSControlCenter.Nut;
using UPSControlCenter.SafetyAgent;

var builder = Host.CreateApplicationBuilder(args);

// systemd: Type=notify con watchdog. L'integrazione rende visibile a systemd lo stato reale
// del servizio invece del solo processo vivo.
builder.Services.AddSystemd();

builder.Services.Configure<SafetyAgentOptions>(
    builder.Configuration.GetSection(SafetyAgentOptions.SectionName));

builder.Services.Configure<NutClientOptions>(
    builder.Configuration.GetSection(NutClientOptions.SectionName));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "SafetyAgent")
    // Formato JSON compatto: i log vanno al journal e devono restare analizzabili.
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

builder.Services.AddSerilog(Log.Logger);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<INutClient, NutTcpClient>();
builder.Services.AddSingleton<SafetyConfigurationValidator>();
builder.Services.AddHttpClient();

// Lo store è l'unico proprietario del file SQLite: singleton, creato dal percorso configurato.
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SafetyAgentOptions>>();
    var log = sp.GetRequiredService<ILogger<SafetyStore>>();
    return new SafetyStore(opts.Value.DatabasePath, log);
});

builder.Services.AddSingleton<ConfigurationSynchronizer>();
builder.Services.AddHostedService<SafetyAgentWorker>();

var host = builder.Build();

try
{
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Il Safety Agent si è arrestato per un errore non gestito.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
