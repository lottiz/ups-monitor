namespace UPSControlCenter.Infrastructure.Options;

/// <summary>Opzioni della sincronizzazione di configurazione verso il Safety Agent (§3.3).</summary>
public sealed class SafetySyncOptions
{
    public const string SectionName = "SafetySync";

    /// <summary>
    /// Chiave HMAC in Base64, da <c>SAFETY_CONFIG_HMAC_KEY</c>. Mai nel codice né nel
    /// repository (§15, §6.6). Minimo 32 byte.
    /// </summary>
    public string? HmacKeyBase64 { get; set; }

    public int PullIntervalSeconds { get; set; } = 60;

    /// <summary>Oltre questa soglia senza sincronizzazione si registra un evento ConfigSyncStale.</summary>
    public int StaleSyncWarningSeconds { get; set; } = 900;

    public byte[] GetHmacKey()
    {
        if (string.IsNullOrWhiteSpace(HmacKeyBase64))
            throw new InvalidOperationException(
                "Chiave HMAC per la configurazione di sicurezza non configurata. " +
                "Impostare SAFETY_CONFIG_HMAC_KEY (almeno 32 byte, generabile con " +
                "'openssl rand -base64 48').");

        var key = Convert.FromBase64String(HmacKeyBase64);
        if (key.Length < 32)
            throw new InvalidOperationException(
                $"La chiave HMAC è di {key.Length} byte: ne sono richiesti almeno 32.");

        return key;
    }
}
