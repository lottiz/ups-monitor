namespace UPSControlCenter.Adapters.Live.Ssh;

/// <summary>Esito dell'esecuzione di un comando SSH.</summary>
public sealed record SshCommandResult
{
    public int? ExitStatus { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// Vero se la connessione si è interrotta durante o subito dopo l'esecuzione del comando.
    /// Per un comando di spegnimento è l'esito <b>atteso</b>, non un errore: il server smette
    /// di rispondere perché si sta arrestando (§10, nota sulla verifica non interpretativa).
    /// </summary>
    public bool DisconnectedDuringExecution { get; init; }
}

/// <summary>
/// Astrae la sessione SSH sottostante, così l'adapter è testabile senza un server SSH reale.
/// L'unica implementazione di produzione (<see cref="SshNetSessionFactory"/>) delega a SSH.NET;
/// i test usano un'implementazione programmabile.
/// </summary>
public interface ISshSession : IAsyncDisposable
{
    Task<SshCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Apre una sessione SSH. <paramref name="fingerprintPolicy"/> decide, dato il fingerprint
/// SHA-256 della chiave host osservata, se la connessione può procedere: è qui che si applica
/// il divieto di <c>StrictHostKeyChecking=no</c> (§8.4) — nessuna connessione si stabilisce
/// senza una decisione esplicita sul fingerprint.
/// </summary>
public interface ISshSessionFactory
{
    Task<ISshSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> fingerprintPolicy, TimeSpan connectTimeout, CancellationToken ct);
}

/// <summary>Eccezione per un fingerprint della chiave host rifiutato dalla policy.</summary>
public sealed class SshHostKeyRejectedException(string fingerprint)
    : Exception($"Fingerprint della chiave host rifiutato dalla policy: {fingerprint}")
{
    public string Fingerprint { get; } = fingerprint;
}
