using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Nut;

/// <summary>
/// Client del protocollo testuale NUT, in <b>sola lettura</b>.
/// <para>
/// Implementa solo <c>LIST VAR</c>, <c>LIST UPS</c> e <c>LOGOUT</c>. Non implementa
/// <c>FSD</c>, <c>INSTCMD</c>, <c>SET VAR</c> né alcun comando di controllo: §15 lo vieta e
/// l'assenza del codice rende la violazione impossibile, non solo scoraggiata.
/// </para>
/// </summary>
public sealed class NutTcpClient(IOptions<NutClientOptions> options, ILogger<NutTcpClient> logger)
    : INutClient
{
    private readonly NutClientOptions _options = options.Value;

    /// <summary>
    /// Ultimo errore di comunicazione segnalato con lo stack trace completo.
    /// <para>
    /// Un UPS non raggiungibile è uno scenario previsto e ricorrente: con un polling a 10
    /// secondi, registrare lo stack a ogni ciclo riempirebbe il journal di righe identiche
    /// rendendo illeggibili gli eventi che contano. Lo stack si registra alla prima occorrenza
    /// e a ogni cambio di errore; le ripetizioni restano a livello Debug.
    /// </para>
    /// </summary>
    private string? _lastReportedError;

    public async Task<IReadOnlyList<string>> ListUpsAsync(CancellationToken ct = default)
    {
        try
        {
            using var session = await NutSession.ConnectAsync(_options, logger, ct);
            var lines = await session.ListAsync("UPS", ct);

            // Formato: UPS <nome> "<descrizione>"
            return [.. lines
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0] == "UPS")
                .Select(p => p[1])];
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException
                                      or OperationCanceledException)
        {
            logger.LogWarning(ex, "Impossibile elencare gli UPS su {Host}:{Port}",
                _options.Host, _options.Port);
            return [];
        }
    }

    /// <summary>
    /// Legge tutte le variabili e costruisce lo snapshot. I problemi di comunicazione non
    /// generano eccezioni: restituiscono uno snapshot non raggiungibile, perché il chiamante è un
    /// loop di monitoraggio che non deve interrompersi (§7.3).
    /// </summary>
    public async Task<UpsSnapshot> ReadAsync(string upsName, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var session = await NutSession.ConnectAsync(_options, logger, ct);
            var lines = await session.ListAsync($"VAR {upsName}", ct);
            var vars = ParseVariables(lines, upsName);

            if (vars.Count == 0)
            {
                return UpsSnapshot.Unreachable(upsName, now,
                    $"Nessuna variabile restituita per l'UPS «{upsName}».");
            }

            if (_lastReportedError is not null)
            {
                logger.LogInformation(
                    "Comunicazione con NUT ripristinata per {Ups} su {Host}:{Port}.",
                    upsName, _options.Host, _options.Port);
                _lastReportedError = null;
            }

            return BuildSnapshot(upsName, now, vars);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            ReportCommunicationFailure(upsName, ex);
            return UpsSnapshot.Unreachable(upsName, now, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout interno, non annullamento richiesto dal chiamante.
            return UpsSnapshot.Unreachable(upsName, now,
                $"Timeout nella lettura di NUT dopo {_options.TimeoutSeconds}s.");
        }
        catch (NutProtocolException ex)
        {
            logger.LogWarning("Errore di protocollo NUT per {Ups}: {Error}", upsName, ex.Message);
            return UpsSnapshot.Unreachable(upsName, now, ex.Message);
        }
    }

    /// <summary>
    /// Registra un errore di comunicazione evitando di ripetere lo stack trace per un guasto
    /// persistente: la prima occorrenza è un avviso completo, le successive restano a Debug
    /// finché l'errore non cambia.
    /// </summary>
    private void ReportCommunicationFailure(string upsName, Exception ex)
    {
        var signature = $"{ex.GetType().Name}:{ex.Message}";

        if (_lastReportedError == signature)
        {
            logger.LogDebug(
                "Lettura NUT ancora non disponibile per {Ups} su {Host}:{Port}: {Error}",
                upsName, _options.Host, _options.Port, ex.Message);
            return;
        }

        _lastReportedError = signature;
        logger.LogWarning(ex, "Lettura NUT fallita per {Ups} su {Host}:{Port}",
            upsName, _options.Host, _options.Port);
    }

    /// <summary>Estrae le coppie nome/valore dalle righe <c>VAR &lt;ups&gt; &lt;nome&gt; "&lt;valore&gt;"</c>.</summary>
    internal static Dictionary<string, string> ParseVariables(IEnumerable<string> lines, string upsName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (!line.StartsWith("VAR ", StringComparison.Ordinal)) continue;

            // VAR <ups> <nome> "<valore con eventuali spazi>"
            var rest = line[4..].TrimStart();
            if (!rest.StartsWith(upsName, StringComparison.OrdinalIgnoreCase)) continue;

            rest = rest[upsName.Length..].TrimStart();
            var sep = rest.IndexOf(' ', StringComparison.Ordinal);
            if (sep <= 0) continue;

            var name = rest[..sep];
            var value = rest[(sep + 1)..].Trim();

            // Il valore è fra apici doppi, con escape \" e \\.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

            result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// Normalizza le variabili NUT. Le variabili assenti restano <c>null</c>: non viene mai
    /// usato un valore di ripiego, perché un dato inventato porterebbe a decisioni errate (§9.3).
    /// </summary>
    internal static UpsSnapshot BuildSnapshot(string upsName, DateTimeOffset now,
        Dictionary<string, string> vars)
    {
        var statusRaw = Get(vars, "ups.status");

        // Variabili di fase: presenti solo su alcune configurazioni trifase (§9.2).
        var phase = vars
            .Where(kv => kv.Key.Contains(".L1", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.Contains(".L2", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.Contains(".L3", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.EndsWith(".phases", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return new UpsSnapshot
        {
            UpsName = upsName,
            AcquiredAt = now,
            StatusRaw = statusRaw,
            Status = UpsStatusParser.Parse(statusRaw),
            CommunicationOk = true,
            IsStale = false,
            BatteryChargePercent = GetDecimal(vars, "battery.charge"),
            BatteryRuntimeSeconds = GetInt(vars, "battery.runtime"),
            BatteryVoltage = GetDecimal(vars, "battery.voltage"),
            InputVoltage = GetDecimal(vars, "input.voltage"),
            OutputVoltage = GetDecimal(vars, "output.voltage"),
            InputFrequency = GetDecimal(vars, "input.frequency"),
            OutputFrequency = GetDecimal(vars, "output.frequency"),
            LoadPercent = GetDecimal(vars, "ups.load"),
            // Alcuni driver trifase non riportano mai il totale, solo output.L1/L2/L3.realpower:
            // la somma delle fasi presenti è un dato reale aggregato, non una stima (§9.3).
            RealPowerWatts = GetDecimal(vars, "ups.realpower") ?? SumPhaseValues(vars, "output", "realpower"),
            OutputCurrentAmps = GetDecimal(vars, "output.current") ?? SumPhaseValues(vars, "output", "current"),
            InputCurrentAmps = GetDecimal(vars, "input.current") ?? SumPhaseValues(vars, "input", "current"),
            Manufacturer = Get(vars, "ups.mfr"),
            Model = Get(vars, "ups.model"),
            Serial = Get(vars, "ups.serial"),
            FirmwareVersion = Get(vars, "ups.firmware"),
            PhaseData = phase,
            RawVariables = vars
        };
    }

    private static string? Get(Dictionary<string, string> vars, string key) =>
        vars.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>
    /// Somma <c>&lt;prefix&gt;.L1.&lt;suffix&gt;</c>, <c>.L2.</c> e <c>.L3.</c> per le fasi
    /// effettivamente presenti. Restituisce <c>null</c> se nessuna fase è riportata: una somma
    /// parziale silenziosa sarebbe un dato inventato tanto quanto un valore a zero (§9.3).
    /// </summary>
    private static decimal? SumPhaseValues(Dictionary<string, string> vars, string prefix, string suffix)
    {
        decimal? total = null;
        foreach (var phase in new[] { "L1", "L2", "L3" })
        {
            var value = GetDecimal(vars, $"{prefix}.{phase}.{suffix}");
            if (value is { } v) total = (total ?? 0m) + v;
        }

        return total;
    }

    /// <summary>Converte in decimal usando la cultura invariante. Un valore non numerico resta null.</summary>
    private static decimal? GetDecimal(Dictionary<string, string> vars, string key)
    {
        var raw = Get(vars, key);
        if (raw is null) return null;
        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    private static int? GetInt(Dictionary<string, string> vars, string key)
    {
        var raw = Get(vars, key);
        if (raw is null) return null;

        // Alcuni driver riportano l'autonomia come decimale: si arrotonda per difetto, mai per eccesso.
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Floor(d) : null;
    }
}

public sealed class NutProtocolException(string message) : Exception(message);

/// <summary>Sessione TCP verso <c>upsd</c>. Incapsula connessione, I/O testuale e chiusura ordinata.</summary>
internal sealed class NutSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NutSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_stream, Encoding.ASCII, 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public static async Task<NutSession> ConnectAsync(NutClientOptions options, ILogger logger,
        CancellationToken ct)
    {
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            client.ReceiveTimeout = options.TimeoutSeconds * 1000;
            client.SendTimeout = options.TimeoutSeconds * 1000;

            var session = new NutSession(client);

            // Autenticazione solo se configurata: le variabili pubbliche non la richiedono.
            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                await session.SendAsync($"USERNAME {options.Username}", timeout.Token);
                await session.ReadLineAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(options.Password))
                {
                    await session.SendAsync($"PASSWORD {options.Password}", timeout.Token);
                    await session.ReadLineAsync(timeout.Token);
                }
            }

            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task SendAsync(string command, CancellationToken ct) =>
        await _writer.WriteLineAsync(command.AsMemory(), ct);

    private async Task<string> ReadLineAsync(CancellationToken ct) =>
        await _reader.ReadLineAsync(ct) ?? throw new NutProtocolException(
            "Connessione chiusa da upsd durante la lettura.");

    /// <summary>
    /// Esegue <c>LIST &lt;what&gt;</c> e raccoglie le righe fino a <c>END LIST</c>.
    /// Un <c>ERR</c> da upsd diventa un'eccezione di protocollo con il codice originale.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAsync(string what, CancellationToken ct)
    {
        await SendAsync($"LIST {what}", ct);

        var first = await ReadLineAsync(ct);
        if (first.StartsWith("ERR ", StringComparison.Ordinal))
            throw new NutProtocolException($"upsd ha risposto con un errore: {first[4..]}");

        if (!first.StartsWith("BEGIN LIST", StringComparison.Ordinal))
            throw new NutProtocolException($"Risposta inattesa da upsd: «{first}».");

        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line.StartsWith("END LIST", StringComparison.Ordinal)) break;
            if (line.StartsWith("ERR ", StringComparison.Ordinal))
                throw new NutProtocolException($"upsd ha risposto con un errore: {line[4..]}");
            lines.Add(line);
        }

        return lines;
    }

    public void Dispose()
    {
        // Chiusura ordinata: LOGOUT evita sessioni pendenti su upsd. Gli errori qui sono irrilevanti.
        try
        {
            _writer.WriteLine("LOGOUT");
            _writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // La connessione è già caduta: non c'è nulla da salvare.
        }

        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}
