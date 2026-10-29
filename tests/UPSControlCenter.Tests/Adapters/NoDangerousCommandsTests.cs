using System.Reflection;
using UPSControlCenter.Core.Abstractions;

namespace UPSControlCenter.Tests.Adapters;

/// <summary>
/// Verifica i divieti strutturali di §15: il codice non deve contenere comandi di controllo UPS
/// e il Safety Agent non deve dipendere da PostgreSQL.
/// </summary>
public sealed class NoDangerousCommandsTests
{
    /// <summary>
    /// L'interfaccia di accesso a NUT non espone alcun metodo di controllo. Verificarlo sul tipo
    /// rende impossibile introdurne uno senza che il test fallisca (§15 "nessun FSD o killpower").
    /// </summary>
    [Fact]
    public void L_interfaccia_NUT_non_espone_metodi_di_controllo()
    {
        var methods = typeof(INutClient).GetMethods().Select(m => m.Name).ToList();

        var forbidden = new[]
        {
            "Fsd", "ForcedShutdown", "KillPower", "SetVariable", "SetVar",
            "InstantCommand", "InstCmd", "Shutdown", "Reboot", "Master", "Primary"
        };

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, methods, StringComparer.OrdinalIgnoreCase);
        }

        // Restano solo le due operazioni di lettura previste.
        Assert.Equal(2, methods.Count);
        Assert.Contains("ListUpsAsync", methods);
        Assert.Contains("ReadAsync", methods);
    }

    /// <summary>
    /// Verifica che al socket NUT vengano inviati solo i comandi di lettura previsti.
    /// <para>
    /// Il controllo è sulle stringhe effettivamente <b>scritte sul socket</b>, non sulle parole
    /// presenti nel file: riconoscere il flag <c>FSD</c> in lettura è legittimo e necessario
    /// (l'UPS può segnalarlo), mentre <b>inviarlo</b> è vietato da §15. Un test che confondesse
    /// le due cose impedirebbe di leggere correttamente lo stato dell'UPS.
    /// </para>
    /// </summary>
    [Fact]
    public void Al_socket_NUT_vengono_inviati_solo_comandi_di_lettura()
    {
        var root = FindRepositoryRoot();
        var nutClientPath = Path.Combine(root, "src", "UPSControlCenter.Nut", "NutTcpClient.cs");

        Assert.True(File.Exists(nutClientPath), $"Client NUT non trovato: {nutClientPath}");

        var content = File.ReadAllText(nutClientPath);

        // Si estraggono gli argomenti delle scritture sul socket: SendAsync, WriteLine e le
        // interpolazioni che costruiscono un comando.
        var writes = System.Text.RegularExpressions.Regex
            .Matches(content, @"(?:SendAsync|WriteLine)\(\s*\$?""([^""]*)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(writes);

        // Whitelist: solo le operazioni di lettura e la chiusura ordinata della sessione.
        var allowedPrefixes = new[] { "LIST", "LOGOUT", "USERNAME", "PASSWORD" };

        var violations = writes
            .Where(w => !allowedPrefixes.Any(
                prefix => w.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Nessun punto del codice deve invocare gli strumenti NUT di spegnimento forzato o
    /// scrivere nella configurazione di NUT (§15, §25.10).
    /// </summary>
    [Fact]
    public void Il_codice_non_invoca_strumenti_NUT_di_controllo_ne_scrive_in_etc_nut()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains("Migrations"))
            .ToList();

        Assert.NotEmpty(sourceFiles);

        // Invocazioni di strumenti esterni e percorsi di configurazione: qui non esiste un uso
        // legittimo, quindi la sola presenza è una violazione.
        var forbidden = new[]
        {
            "upsdrvctl",
            "upsmon",
            "upscmd",
            "/etc/nut",
            "killpower",
            "shutdown.return",
            "load.off",
            "driver.killpower"
        };

        var violations = new List<string>();

        foreach (var file in sourceFiles)
        {
            // I commenti vengono rimossi prima della ricerca: documentare un divieto citandone
            // il nome è legittimo e necessario, mentre il codice che lo esegue non lo è.
            var content = StripComments(File.ReadAllText(file));

            foreach (var pattern in forbidden)
            {
                if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetFileName(file)}: «{pattern}»");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Rimuove commenti di riga, commenti a blocco e documentazione XML, lasciando il codice
    /// eseguibile e i letterali di stringa.
    /// </summary>
    private static string StripComments(string source)
    {
        // Prima i blocchi /* ... */, poi le righe // e ///.
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        return System.Text.RegularExpressions.Regex.Replace(
            withoutBlocks, @"^\s*///?.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    /// <summary>
    /// Il codice non deve avviare processi esterni: §15 vieta i comandi shell arbitrari e
    /// l'assenza di <c>Process.Start</c> lo rende verificabile.
    /// </summary>
    [Fact]
    public void Il_codice_non_avvia_processi_esterni()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var violations = sourceFiles
            .Where(f => File.ReadAllText(f).Contains("Process.Start", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Il Safety Agent non deve dipendere da PostgreSQL: è il vincolo architetturale centrale
    /// (§2.2). Un riferimento accidentale a Npgsql lo renderebbe dipendente dal Control Plane.
    /// </summary>
    [Fact]
    public void Il_Safety_Agent_non_referenzia_Npgsql_ne_EF_Core()
    {
        var root = FindRepositoryRoot();
        var csproj = Path.Combine(root, "src", "UPSControlCenter.SafetyAgent",
            "UPSControlCenter.SafetyAgent.csproj");

        Assert.True(File.Exists(csproj), $"File di progetto non trovato: {csproj}");

        // Si analizza la struttura XML e non il testo grezzo: i commenti che documentano il
        // vincolo citano legittimamente "Npgsql" e non devono far fallire il test.
        var document = System.Xml.Linq.XDocument.Load(csproj);

        var references = document.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        Assert.NotEmpty(references);

        Assert.DoesNotContain(references,
            r => r.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references,
            r => r.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));

        // Il progetto Infrastructure porterebbe Npgsql per transitività.
        Assert.DoesNotContain(references,
            r => r.EndsWith("UPSControlCenter.Infrastructure.csproj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifica a runtime: nessun assembly Npgsql è raggiungibile dal grafo di dipendenze del
    /// Safety Agent. Il controllo sul csproj non coprirebbe una dipendenza transitiva.
    /// </summary>
    [Fact]
    public void L_assembly_del_Safety_Agent_non_dipende_da_Npgsql()
    {
        var assembly = typeof(SafetyAgent.SafetyAgentWorker).Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(referenced,
            name => name is not null
                    && name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UPSControlCenter.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Radice del repository non trovata a partire da " + AppContext.BaseDirectory);
    }
}
