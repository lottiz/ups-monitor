using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace UPSControlCenter.Adapters.Live.WinRm;

/// <summary>
/// Implementazione reale di <see cref="IWinRmSessionFactory"/> tramite WS-Management
/// (protocollo "winrs"/PowerShell remoting di base: Create/Command/Receive/Signal/Delete sulla
/// risorsa shell cmd), scritta direttamente sopra <see cref="HttpClient"/> perché non esiste una
/// libreria .NET multipiattaforma matura equivalente a SSH.NET per questo protocollo.
/// <para>
/// <b>Non ancora validata contro un host Windows reale</b> (nessun target WinRM disponibile in
/// questo ambiente di sviluppo): la struttura delle busta SOAP segue la specifica pubblica
/// MS-WSMV, ma va verificata end-to-end contro un server Windows prima di essere abilitata in
/// LIVE (vedi docs/14-fase3.md). La logica di decisione dell'adapter (gating, pinning del
/// certificato, comando fisso) è invece pienamente testata.
/// </para>
/// </summary>
public sealed class WinRmHttpSessionFactory : IWinRmSessionFactory
{
    private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    private const string NsAddr = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private const string NsWsman = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private const string NsShell = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private const string ResourceUriShell = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";

    public async Task<IWinRmSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> certificateThumbprintPolicy, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(credential.Username, credential.Secret),
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                var thumbprint = Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
                return certificateThumbprintPolicy(thumbprint);
            },
        };

        var client = new HttpClient(handler) { Timeout = connectTimeout, BaseAddress = new Uri($"https://{host}:{port}/wsman") };

        string shellId;
        try
        {
            var createResponse = await PostAsync(client,
                BuildEnvelope("http://schemas.xmlsoap.org/ws/2004/09/transfer/Create", ResourceUriShell, null,
                    new XElement(XName.Get("Shell", NsShell),
                        new XElement(XName.Get("InputStreams", NsShell), "stdin"),
                        new XElement(XName.Get("OutputStreams", NsShell), "stdout stderr"))),
                ct);
            shellId = ExtractShellId(createResponse);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            client.Dispose();
            throw new WinRmAuthenticationException("Autenticazione WinRM rifiutata dall'host.");
        }

        return new WinRmHttpSession(client, shellId);
    }

    private static async Task<XDocument> PostAsync(HttpClient client, XDocument envelope, CancellationToken ct)
    {
        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };

        using var response = await client.PostAsync(string.Empty, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"WinRM ha risposto {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        return XDocument.Parse(body);
    }

    private static XDocument BuildEnvelope(string action, string resourceUri, string? shellId, XElement body)
    {
        var header = new XElement(XName.Get("Header", NsSoap),
            new XElement(XName.Get("Action", NsAddr), action),
            new XElement(XName.Get("To", NsAddr), "https://windows-host:5986/wsman"),
            new XElement(XName.Get("ResourceURI", NsWsman), resourceUri),
            new XElement(XName.Get("MessageID", NsAddr), $"uuid:{Guid.NewGuid()}"));

        if (shellId is not null)
        {
            header.Add(new XElement(XName.Get("SelectorSet", NsWsman),
                new XElement(XName.Get("Selector", NsWsman),
                    new XAttribute("Name", "ShellId"), shellId)));
        }

        return new XDocument(
            new XElement(XName.Get("Envelope", NsSoap),
                header,
                new XElement(XName.Get("Body", NsSoap), body)));
    }

    private static string ExtractShellId(XDocument response)
    {
        var shellId = response.Descendants(XName.Get("ShellId", NsShell)).FirstOrDefault()?.Value
            ?? response.Descendants(XName.Get("Selector", NsWsman))
                .FirstOrDefault(e => (string?)e.Attribute("Name") == "ShellId")?.Value;

        return shellId ?? throw new InvalidOperationException(
            "Risposta WinRM senza ShellId: creazione della shell non riuscita.");
    }

    internal static XDocument BuildCommandEnvelope(string shellId, string commandLine) =>
        BuildEnvelope("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command",
            ResourceUriShell, shellId,
            new XElement(XName.Get("CommandLine", NsShell),
                new XElement(XName.Get("Command", NsShell), commandLine)));

    internal static XDocument BuildReceiveEnvelope(string shellId) =>
        BuildEnvelope("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive",
            ResourceUriShell, shellId,
            new XElement(XName.Get("Receive", NsShell),
                new XElement(XName.Get("DesiredStream", NsShell), "stdout stderr")));

    internal static XDocument BuildSignalEnvelope(string shellId, string commandId) =>
        BuildEnvelope("http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal",
            ResourceUriShell, shellId,
            new XElement(XName.Get("Signal", NsShell),
                new XAttribute("CommandId", commandId),
                new XElement(XName.Get("Code", NsShell),
                    "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate")));

    internal static XDocument BuildDeleteEnvelope(string shellId) =>
        BuildEnvelope("http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", ResourceUriShell, shellId,
            new XElement(XName.Get("Delete", NsSoap)));

    private sealed class WinRmHttpSession(HttpClient client, string shellId) : IWinRmSession
    {
        public async Task<WinRmCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
        {
            var commandResponse = await PostAsync(client, BuildCommandEnvelope(shellId, command), ct);
            var commandId = commandResponse.Descendants(XName.Get("CommandId", NsShell)).FirstOrDefault()?.Value
                ?? throw new InvalidOperationException("Risposta WinRM senza CommandId.");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            int? exitCode = null;
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var receiveResponse = await PostAsync(client, BuildReceiveEnvelope(shellId), ct);

                foreach (var stream in receiveResponse.Descendants(XName.Get("Stream", NsShell)))
                {
                    var text = (string?)stream;
                    if (string.IsNullOrEmpty(text)) continue;
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                    var name = (string?)stream.Attribute("Name");
                    (name == "stderr" ? stderr : stdout).Append(decoded);
                }

                var commandState = receiveResponse.Descendants(XName.Get("CommandState", NsShell)).FirstOrDefault();
                var state = (string?)commandState?.Attribute("State");
                if (state is not null && state.EndsWith("/Done", StringComparison.Ordinal))
                {
                    var exitCodeText = commandState!.Descendants(XName.Get("ExitCode", NsShell)).FirstOrDefault()?.Value;
                    exitCode = int.TryParse(exitCodeText, out var parsed) ? parsed : null;
                    break;
                }
            }

            try
            {
                await PostAsync(client, BuildSignalEnvelope(shellId, commandId), ct);
            }
            catch (HttpRequestException)
            {
                // L'host potrebbe aver già chiuso il comando: non è un errore per chi chiama,
                // il risultato già raccolto resta valido.
            }

            return new WinRmCommandResult(exitCode, stdout.ToString(), stderr.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await PostAsync(client, BuildDeleteEnvelope(shellId), CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                // Chiusura best-effort della shell remota: una connessione già persa non deve
                // impedire la dispose locale delle risorse.
            }

            client.Dispose();
        }
    }
}
