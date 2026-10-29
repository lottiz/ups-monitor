namespace UPSControlCenter.SafetyAgent;

public sealed class SafetyAgentOptions
{
    public const string SectionName = "SafetyAgent";

    /// <summary>Database locale. L'agent ne è l'unico proprietario (§3.2).</summary>
    public string DatabasePath { get; set; } = "/var/lib/ups-control-center/safety.db";

    /// <summary>File sentinella del kill switch, condiviso con il Control Plane (§8.3).</summary>
    public string KillSwitchPath { get; set; } = "/var/lib/ups-control-center/AUTOMATION_DISABLED";

    /// <summary>
    /// Endpoint del portale per il pull della configurazione. L'agent chiama, non viene
    /// chiamato: non apre porte in ingresso (§1.3).
    /// </summary>
    public string? ControlPlaneBaseUrl { get; set; } = "https://127.0.0.1:8443";

    /// <summary>Chiave HMAC in Base64 per verificare la firma della configurazione (§3.3).</summary>
    public string? ConfigHmacKeyBase64 { get; set; }

    /// <summary>
    /// Token condiviso con cui l'agent si autentica verso il portale. L'agent è un servizio
    /// systemd senza sessione interattiva, quindi non può usare i cookie di Identity.
    /// </summary>
    public string? AgentToken { get; set; }

    public int ConfigPullIntervalSeconds { get; set; } = 60;

    /// <summary>Intervallo di polling usato finché non è disponibile una configurazione attiva.</summary>
    public int FallbackPollingIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Se vero, l'agent accetta configurazioni senza verificare la firma. <b>Solo per sviluppo</b>:
    /// in produzione lascia passare configurazioni non autenticate.
    /// </summary>
    public bool AllowUnsignedConfigForDevelopment { get; set; }
}
