namespace UPSControlCenter.Infrastructure.Security;

/// <summary>
/// Cifratura dei segreti applicativi. L'implementazione concreta usa ASP.NET Core Data
/// Protection con chiavi su volume persistente dedicato (§6.5, §8.4).
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Cifra un segreto. Il purpose separa i domini: una chiave SSH non è decifrabile come password SMTP.</summary>
    byte[] Protect(string plaintext, string purpose);

    /// <summary>
    /// Decifra. Restituisce <c>null</c> se il dato non è decifrabile (chiavi perse o ruotate),
    /// senza sollevare eccezioni: la perdita del volume delle chiavi è uno scenario previsto (R12).
    /// </summary>
    string? Unprotect(byte[] ciphertext, string purpose);

    /// <summary>Identificativo della chiave corrente, memorizzato con il segreto per la rotazione.</summary>
    string CurrentKeyId { get; }
}

public static class ProtectionPurposes
{
    public const string ConnectionProfileSecret = "UPSControlCenter.ConnectionProfile.v1";
    public const string NotificationChannelSecret = "UPSControlCenter.NotificationChannel.v1";
    public const string SafetyConfigHmac = "UPSControlCenter.SafetyConfig.Hmac.v1";
}
