using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace UPSControlCenter.Infrastructure.Security;

/// <summary>
/// Implementazione su ASP.NET Core Data Protection. Le chiavi risiedono sul volume
/// <c>ups-dataprotection</c>, il cui backup è un passo obbligatorio: perderlo rende illeggibili
/// tutte le credenziali salvate (§6.5, R12).
/// </summary>
public sealed class DataProtectionCredentialProtector(
    IDataProtectionProvider provider,
    ILogger<DataProtectionCredentialProtector> logger) : ICredentialProtector
{
    public string CurrentKeyId => "dp-v1";

    public byte[] Protect(string plaintext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        return provider.CreateProtector(purpose).Protect(
            System.Text.Encoding.UTF8.GetBytes(plaintext));
    }

    public string? Unprotect(byte[] ciphertext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length == 0) return null;

        try
        {
            var plain = provider.CreateProtector(purpose).Unprotect(ciphertext);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Il valore in chiaro non viene mai registrato nei log, né il ciphertext (§8.4).
            logger.LogError(ex,
                "Impossibile decifrare un segreto con purpose {Purpose}: chiavi Data Protection " +
                "mancanti o ruotate. Verificare il backup del volume delle chiavi.", purpose);
            return null;
        }
    }
}
