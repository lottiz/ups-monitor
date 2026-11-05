using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Server Redfish minimale e reale (Kestrel + HTTPS con certificato autofirmato generato al
/// volo), usato per verificare <see cref="UPSControlCenter.Adapters.Live.Redfish.RedfishDeviceAdapter"/>
/// end-to-end: richieste HTTP vere, certificato vero, non una simulazione del trasporto (§21,
/// coerente con l'approccio già usato per NUT in Fase 1).
/// </summary>
internal sealed class FakeRedfishServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;

    public int Port { get; private set; }
    public string? LastResetBody { get; private set; }
    public string PowerState { get; set; } = "On";
    public bool RejectAuth { get; set; }

    /// <summary>Thumbprint SHA-256 del certificato, nello stesso formato usato per il pinning (§8.4).</summary>
    public string CertificateThumbprintSha256 =>
        Convert.ToHexStringLower(_certificate.GetCertHash(HashAlgorithmName.SHA256));

    private FakeRedfishServer(WebApplication app, X509Certificate2 certificate, int port)
    {
        _app = app;
        _certificate = certificate;
        Port = port;
    }

    public static async Task<FakeRedfishServer> StartAsync()
    {
        var certificate = CreateSelfSignedCertificate();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listenOptions =>
                listenOptions.UseHttps(certificate));
        });

        var app = builder.Build();
        var state = new FakeRedfishServer(app, certificate, 0);

        app.MapGet("/redfish/v1/", () => Results.Text("""{"RedfishVersion":"1.6.0"}""",
            "application/json"));

        app.MapGet("/redfish/v1/Systems/1", (HttpContext ctx) =>
        {
            if (!CheckAuth(ctx, state)) return Results.Unauthorized();
            // Scritto a mano con il nome di proprietà esatto in PascalCase: Results.Json()
            // applicherebbe la naming policy camelCase di default di ASP.NET Core, mentre il
            // vero standard Redfish (DMTF) usa PascalCase, come l'adapter correttamente richiede.
            return Results.Text($$"""{"PowerState":"{{state.PowerState}}"}""", "application/json");
        });

        app.MapPost("/redfish/v1/Systems/1/Actions/ComputerSystem.Reset",
            async (HttpContext ctx) =>
            {
                if (!CheckAuth(ctx, state)) return Results.Unauthorized();
                using var reader = new StreamReader(ctx.Request.Body);
                state.LastResetBody = await reader.ReadToEndAsync();
                return Results.NoContent();
            });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        // Si aggiorna la STESSA istanza usata dagli handler delle route (chiudono su "state"):
        // crearne una seconda qui significherebbe che il test osserva e muta un oggetto diverso
        // da quello che il server usa davvero per rispondere.
        state.Port = int.Parse(address.Split(':')[^1]);

        return state;
    }

    private static bool CheckAuth(HttpContext ctx, FakeRedfishServer state) => !state.RejectAuth;

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake-bmc.local", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Esportare e reimportare come PFX garantisce che la chiave privata sia utilizzabile da
        // Kestrel su tutte le piattaforme (alcune combinazioni OpenSSL altrimenti falliscono).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
    }
}
