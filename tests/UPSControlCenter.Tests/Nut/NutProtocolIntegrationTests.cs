using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Nut;
using UPSControlCenter.Nut;

namespace UPSControlCenter.Tests.Nut;

/// <summary>
/// Server NUT minimale per i test: parla il protocollo testuale di <c>upsd</c> su una porta
/// effimera. Permette di verificare il client end-to-end, cosa che il solo test del parsing
/// non copre (dialogo, terminatori di riga, BEGIN/END LIST, errori, chiusura).
/// </summary>
internal sealed class FakeNutServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly IReadOnlyList<string> _variables;
    private readonly string? _errorCode;
    private readonly bool _closeImmediately;

    public int Port { get; }

    /// <summary>Comandi ricevuti dal client: permette di verificare cosa viene effettivamente inviato.</summary>
    public List<string> ReceivedCommands { get; } = [];

    public FakeNutServer(IReadOnlyList<string> variables, string? errorCode = null,
        bool closeImmediately = false)
    {
        _variables = variables;
        _errorCode = errorCode;
        _closeImmediately = closeImmediately;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }
        catch (OperationCanceledException) { /* arresto richiesto */ }
        catch (SocketException) { /* listener chiuso */ }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            if (_closeImmediately) return;

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (ReceivedCommands) { ReceivedCommands.Add(line); }

                if (line.StartsWith("LOGOUT", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("OK Goodbye");
                    return;
                }

                if (line.StartsWith("USERNAME", StringComparison.Ordinal)
                    || line.StartsWith("PASSWORD", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("OK");
                    continue;
                }

                if (line.StartsWith("LIST VAR", StringComparison.Ordinal))
                {
                    if (_errorCode is not null)
                    {
                        await writer.WriteLineAsync($"ERR {_errorCode}");
                        continue;
                    }

                    var upsName = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    await writer.WriteLineAsync($"BEGIN LIST VAR {upsName}");
                    foreach (var v in _variables) await writer.WriteLineAsync(v);
                    await writer.WriteLineAsync($"END LIST VAR {upsName}");
                    continue;
                }

                if (line.StartsWith("LIST UPS", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("BEGIN LIST UPS");
                    await writer.WriteLineAsync("UPS riello \"UPS principale\"");
                    await writer.WriteLineAsync("UPS secondario \"UPS di riserva\"");
                    await writer.WriteLineAsync("END LIST UPS");
                    continue;
                }

                await writer.WriteLineAsync("ERR UNKNOWN-COMMAND");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();

        try { await _acceptLoop; }
        catch (OperationCanceledException) { /* atteso */ }

        _cts.Dispose();
    }
}

/// <summary>
/// Verifica il client NUT contro un server che parla il protocollo reale. Copre il dialogo
/// completo, non solo l'interpretazione delle stringhe.
/// </summary>
public sealed class NutProtocolIntegrationTests
{
    private static readonly string[] RielloVariables =
    [
        "VAR riello battery.charge \"87\"",
        "VAR riello battery.runtime \"2640\"",
        "VAR riello battery.voltage \"54.2\"",
        "VAR riello input.voltage \"0.0\"",
        "VAR riello output.voltage \"230.1\"",
        "VAR riello ups.load \"31\"",
        "VAR riello ups.status \"OB DISCHRG\"",
        "VAR riello ups.mfr \"RPS S.p.a.\"",
        "VAR riello ups.model \"DS3MK10CS100RUA\""
    ];

    private static NutTcpClient CreateClient(int port, string? username = null,
        int timeoutSeconds = 5)
    {
        var options = Options.Create(new NutClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            UpsName = "riello",
            TimeoutSeconds = timeoutSeconds,
            Username = username
        });

        return new NutTcpClient(options, NullLogger<NutTcpClient>.Instance);
    }

    [Fact]
    public async Task Il_client_legge_le_variabili_da_un_server_NUT_reale()
    {
        await using var server = new FakeNutServer(RielloVariables);
        var client = CreateClient(server.Port);

        var snapshot = await client.ReadAsync("riello");

        Assert.True(snapshot.CommunicationOk);
        Assert.True(snapshot.Status.IsOnBattery());
        Assert.Equal(87m, snapshot.BatteryChargePercent);
        Assert.Equal(2640, snapshot.BatteryRuntimeSeconds);
        Assert.Equal(31m, snapshot.LoadPercent);
        Assert.Equal("RPS S.p.a.", snapshot.Manufacturer);
        Assert.Equal("DS3MK10CS100RUA", snapshot.Model);
        Assert.True(snapshot.IsUsableForDecisions);
    }

    [Fact]
    public async Task Il_client_invia_solo_comandi_di_lettura()
    {
        await using var server = new FakeNutServer(RielloVariables);
        var client = CreateClient(server.Port);

        await client.ReadAsync("riello");

        // Verifica sul traffico effettivo: nessun comando di controllo raggiunge upsd (§15).
        List<string> commands;
        lock (server.ReceivedCommands) { commands = [.. server.ReceivedCommands]; }

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.True(
            c.StartsWith("LIST ", StringComparison.Ordinal)
            || c.StartsWith("LOGOUT", StringComparison.Ordinal),
            $"Comando inatteso inviato a upsd: «{c}»"));
    }

    [Fact]
    public async Task Il_client_elenca_gli_UPS_configurati()
    {
        await using var server = new FakeNutServer(RielloVariables);
        var client = CreateClient(server.Port);

        var list = await client.ListUpsAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains("riello", list);
        Assert.Contains("secondario", list);
    }

    [Fact]
    public async Task Un_errore_di_upsd_produce_uno_snapshot_non_raggiungibile_senza_eccezioni()
    {
        await using var server = new FakeNutServer([], errorCode: "UNKNOWN-UPS");
        var client = CreateClient(server.Port);

        // Il loop di monitoraggio non deve interrompersi per un errore di protocollo.
        var snapshot = await client.ReadAsync("inesistente");

        Assert.False(snapshot.CommunicationOk);
        Assert.True(snapshot.Status.IsUnknown());
        Assert.Null(snapshot.BatteryRuntimeSeconds);
        Assert.Contains("UNKNOWN-UPS", snapshot.CommunicationError);
    }

    [Fact]
    public async Task Una_connessione_chiusa_subito_non_solleva_eccezioni()
    {
        await using var server = new FakeNutServer([], closeImmediately: true);
        var client = CreateClient(server.Port);

        var snapshot = await client.ReadAsync("riello");

        Assert.False(snapshot.CommunicationOk);
        Assert.True(snapshot.IsStale);
        Assert.False(snapshot.IsUsableForDecisions);
    }

    [Fact]
    public async Task Un_server_assente_produce_uno_snapshot_non_raggiungibile()
    {
        // Porta su cui nessuno ascolta: è lo scenario "NUT giù" del §21.
        var client = CreateClient(59999);

        var snapshot = await client.ReadAsync("riello");

        Assert.False(snapshot.CommunicationOk);
        Assert.True(snapshot.Status.IsUnknown());
        // Nessuna metrica inventata quando la comunicazione fallisce.
        Assert.Null(snapshot.BatteryChargePercent);
        Assert.Null(snapshot.BatteryRuntimeSeconds);
        Assert.Null(snapshot.LoadPercent);
    }

    [Fact]
    public async Task L_autenticazione_viene_inviata_solo_se_configurata()
    {
        await using var withAuth = new FakeNutServer(RielloVariables);
        await CreateClient(withAuth.Port, username: "monitor").ReadAsync("riello");

        List<string> authCommands;
        lock (withAuth.ReceivedCommands) { authCommands = [.. withAuth.ReceivedCommands]; }
        Assert.Contains(authCommands, c => c.StartsWith("USERNAME", StringComparison.Ordinal));

        await using var noAuth = new FakeNutServer(RielloVariables);
        await CreateClient(noAuth.Port).ReadAsync("riello");

        List<string> plainCommands;
        lock (noAuth.ReceivedCommands) { plainCommands = [.. noAuth.ReceivedCommands]; }
        Assert.DoesNotContain(plainCommands,
            c => c.StartsWith("USERNAME", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_UPS_che_non_fornisce_l_autonomia_produce_un_valore_nullo()
    {
        // Riproduce un driver che non espone battery.runtime: il caso che R5 considera critico.
        string[] partial =
        [
            "VAR riello ups.status \"OB\"",
            "VAR riello battery.charge \"45\""
        ];

        await using var server = new FakeNutServer(partial);
        var snapshot = await CreateClient(server.Port).ReadAsync("riello");

        Assert.True(snapshot.CommunicationOk);
        Assert.Equal(45m, snapshot.BatteryChargePercent);
        Assert.Null(snapshot.BatteryRuntimeSeconds);
    }

    [Fact]
    public async Task Letture_consecutive_funzionano_senza_perdere_la_connessione()
    {
        // Il loop di polling apre e chiude una sessione per ciclo: non deve esaurire risorse
        // né lasciare sessioni pendenti su upsd.
        await using var server = new FakeNutServer(RielloVariables);
        var client = CreateClient(server.Port);

        for (var i = 0; i < 10; i++)
        {
            var snapshot = await client.ReadAsync("riello");
            Assert.True(snapshot.CommunicationOk, $"Lettura {i + 1} non riuscita.");
        }

        // Il client chiude il socket subito dopo aver scritto LOGOUT, senza attendere la
        // risposta: l'elaborazione lato server (che registra il comando) è quindi asincrona
        // rispetto al ritorno di ReadAsync. Si attende con un breve timeout invece di assumere
        // che sia già avvenuta, altrimenti il test diventa intermittente sotto carico (il
        // fallimento osservato non indicava una sessione persa, ma un'asserzione anticipata).
        List<string> commands = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (server.ReceivedCommands) { commands = [.. server.ReceivedCommands]; }
            if (commands.Count(c => c.StartsWith("LOGOUT", StringComparison.Ordinal)) >= 10) break;
            await Task.Delay(20);
        }

        // Ogni sessione chiude con LOGOUT: nessuna sessione lasciata aperta.
        Assert.Equal(10, commands.Count(c => c.StartsWith("LOGOUT", StringComparison.Ordinal)));
    }
}
