namespace UPSControlCenter.Adapters.Live.WinRm;

/// <summary>Esito dell'esecuzione di un comando in una sessione WinRM.</summary>
public sealed record WinRmCommandResult(int? ExitCode, string Stdout, string Stderr);

/// <summary>
/// Sessione WinRM aperta verso un host. Esegue esattamente un comando per chiamata: non è un
/// canale di shell generico riutilizzabile per comandi arbitrari (§8.3, stesso vincolo di
/// <see cref="Ssh.ISshSession"/>).
/// </summary>
public interface IWinRmSession : IAsyncDisposable
{
    Task<WinRmCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Il certificato TLS presentato dall'host non è stato accettato dalla policy del fingerprint.</summary>
public sealed class WinRmCertificateRejectedException(string message) : Exception(message);

/// <summary>Autenticazione WinRM rifiutata dall'host (credenziali non valide).</summary>
public sealed class WinRmAuthenticationException(string message) : Exception(message);

/// <summary>
/// Apre sessioni WinRM (WS-Management). Astratto dalla connessione reale per rendere testabile
/// la logica decisionale dell'adapter senza un vero host Windows (§24, stesso approccio di
/// <see cref="Ssh.ISshSessionFactory"/>).
/// </summary>
public interface IWinRmSessionFactory
{
    /// <param name="certificateThumbprintPolicy">
    /// Invocata con il thumbprint SHA-256 del certificato TLS presentato dall'host, prima di
    /// completare la connessione: deve restituire <c>true</c> per accettarlo. Non esiste un
    /// percorso WinRM che bypassi questo controllo (nessun equivalente di
    /// <c>StrictHostKeyChecking=no</c>, §8.4) — solo HTTPS è supportato.
    /// </param>
    Task<IWinRmSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> certificateThumbprintPolicy, TimeSpan connectTimeout,
        CancellationToken ct = default);
}
