namespace UPSControlCenter.Nut;

public sealed class NutClientOptions
{
    public const string SectionName = "Nut";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3493;
    public string UpsName { get; set; } = "riello";

    /// <summary>Timeout di connessione e lettura. Volutamente breve: un UPS lento non deve bloccare il loop.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    public int PollingIntervalSeconds { get; set; } = 10;
    public int StaleDataThresholdSeconds { get; set; } = 60;

    /// <summary>
    /// Credenziali di <b>sola lettura</b>. NUT non le richiede per le variabili pubbliche; se
    /// configurate, vengono usate solo con <c>USERNAME</c>/<c>PASSWORD</c>.
    /// Nessuna credenziale di controllo (upsmon master) è prevista o accettata (§15).
    /// </summary>
    public string? Username { get; set; }
    public string? Password { get; set; }
}
