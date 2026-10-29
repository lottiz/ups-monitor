using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Infrastructure.Adapters;
using UPSControlCenter.Infrastructure.Options;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Infrastructure.Security;
using UPSControlCenter.Infrastructure.Services;
using UPSControlCenter.Nut;
using UPSControlCenter.Web.Hubs;
using UPSControlCenter.Web.Security;
using UPSControlCenter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "Web")
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

builder.Host.UseSerilog(Log.Logger);

// ---------------------------------------------------------------- database
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Stringa di connessione «Postgres» non configurata. " +
        "Impostare ConnectionStrings__Postgres nell'ambiente.");

builder.Services.AddDbContext<UpsDbContext>(options => options
    .UseNpgsql(connectionString, npg => npg
        .MigrationsHistoryTable("__ef_migrations_history", UpsDbContext.Schema)
        .EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
    // Deve coincidere con la convenzione usata per generare le migrazioni, altrimenti il
    // modello non corrisponderebbe allo schema.
    .UseSnakeCaseNamingConvention());

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------------------------------------------------------------- identity
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Lockout: difesa contro il credential stuffing sul portale.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<UpsDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

// ---------------------------------------------------------------- autorizzazione
// Policy nominate: le capability sono definite in un unico punto, non sparse nelle view (§8.6).
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.ViewDashboard, p => p.RequireRole(
        ApplicationRoles.Administrator, ApplicationRoles.Operator, ApplicationRoles.Viewer))
    .AddPolicy(Policies.RunSimulation, p => p.RequireRole(
        ApplicationRoles.Administrator, ApplicationRoles.Operator))
    .AddPolicy(Policies.ManageDevices, p => p.RequireRole(
        ApplicationRoles.Administrator, ApplicationRoles.Operator))
    .AddPolicy(Policies.EditRules, p => p.RequireRole(
        ApplicationRoles.Administrator, ApplicationRoles.Operator))
    .AddPolicy(Policies.UseKillSwitch, p => p.RequireRole(
        ApplicationRoles.Administrator, ApplicationRoles.Operator))
    // Operazioni irreversibili o che abilitano azioni reali: solo Administrator.
    .AddPolicy(Policies.PublishConfiguration, p => p.RequireRole(ApplicationRoles.Administrator))
    .AddPolicy(Policies.ManageCredentials, p => p.RequireRole(ApplicationRoles.Administrator))
    .AddPolicy(Policies.EnableLive, p => p.RequireRole(ApplicationRoles.Administrator))
    .AddPolicy(Policies.ManageUsers, p => p.RequireRole(ApplicationRoles.Administrator));

// ---------------------------------------------------------------- data protection
// Chiavi su volume persistente: perderle rende illeggibili le credenziali salvate (§6.5, R12).
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"] ?? "/keys";
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("UPSControlCenter");

// ---------------------------------------------------------------- opzioni
builder.Services.Configure<NutClientOptions>(
    builder.Configuration.GetSection(NutClientOptions.SectionName));
builder.Services.Configure<SafetySyncOptions>(
    builder.Configuration.GetSection(SafetySyncOptions.SectionName));
builder.Services.Configure<KillSwitchOptions>(
    builder.Configuration.GetSection(KillSwitchOptions.SectionName));
builder.Services.Configure<NetworkRestrictionOptions>(
    builder.Configuration.GetSection(NetworkRestrictionOptions.SectionName));

// ---------------------------------------------------------------- servizi
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<INutClient, NutTcpClient>();
builder.Services.AddSingleton<IKillSwitch, FileKillSwitch>();
builder.Services.AddSingleton<SafetyConfigurationValidator>();
builder.Services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
builder.Services.AddScoped<IConfigurationPublisher, ConfigurationPublisher>();
builder.Services.AddScoped<DashboardStateService>();
builder.Services.AddHostedService<DashboardBroadcastService>();

// Modalità effettiva: DRY_RUN salvo richiesta esplicita E autorizzazione di deployment (§8.2).
var executionOptions = new ExecutionModeOptions
{
    Mode = Enum.TryParse<ExecutionMode>(builder.Configuration["Execution:Mode"], true, out var m)
        ? m
        : ExecutionMode.DryRun,
    DeploymentAllowsLive = Environment.GetEnvironmentVariable("UPS_ALLOW_LIVE") == "1"
};
builder.Services.AddDeviceAdapters(executionOptions);

// ---------------------------------------------------------------- rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("api", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 100;
        o.QueueLimit = 0;
    });

    // Limite stretto sulle operazioni sensibili: pubblicazione, kill switch, abilitazione LIVE.
    options.AddFixedWindowLimiter("sensitive", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 5;
        o.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("login", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 5;
        o.QueueLimit = 0;
    });
});

// ---------------------------------------------------------------- health checks
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<OrchestratorHealthCheck>("orchestrator")
    .AddCheck<ExecutionModeHealthCheck>("execution-mode");

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();

// Proxy inverso: i KnownProxies vanno dichiarati, altrimenti X-Forwarded-For è falsificabile
// e il filtro IP applicativo diventerebbe aggirabile (§8.5).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    foreach (var proxy in builder.Configuration.GetSection("KnownProxies").Get<string[]>() ?? [])
    {
        if (System.Net.IPAddress.TryParse(proxy, out var ip))
            options.KnownProxies.Add(ip);
    }
});

var app = builder.Build();

// ---------------------------------------------------------------- modalità "migrate"
// Le migrazioni girano in un job dedicato e non all'avvio dell'app: più istanze che migrano in
// parallelo produrrebbero una race sullo schema (§6.9, R14).
if (args.Contains("migrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<UpsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applicazione delle migrazioni…");
    await db.Database.MigrateAsync();

    await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration, logger);
    await SeedData.SeedAsync(db, logger);

    logger.LogInformation("Migrazioni e dati iniziali applicati.");
    return 0;
}

// ---------------------------------------------------------------- pipeline
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();

// Intestazioni di sicurezza. La CSP è restrittiva: nessun eval, nessun inline non necessario.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";
    await next();
});

// Filtro di rete prima dell'autenticazione: una richiesta da rete non autorizzata non deve
// nemmeno raggiungere la pagina di login (§8.5).
app.UseMiddleware<NetworkRestrictionMiddleware>();

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");
app.MapRazorPages();
app.MapHub<DashboardHub>("/hubs/dashboard");

// /health/live: il processo risponde. /health/ready: dipendenze verificate (§6.4).
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready").AllowAnonymous();

Log.Information(
    "UPS Control Center avviato. Modalità effettiva: {Mode} (richiesta {Requested}, " +
    "autorizzazione deployment {Allowed}).",
    executionOptions.EffectiveMode, executionOptions.Mode, executionOptions.DeploymentAllowsLive);

try
{
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Il portale si è arrestato per un errore non gestito.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
