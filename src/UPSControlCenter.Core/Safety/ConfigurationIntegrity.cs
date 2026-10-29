using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Core.Safety;

/// <summary>Risultato della verifica di integrità e autenticità di una configurazione.</summary>
public sealed record IntegrityVerification
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static IntegrityVerification Ok() => new() { IsValid = true };
    public static IntegrityVerification Invalid(params string[] errors) =>
        new() { IsValid = false, Errors = errors };
}

/// <summary>
/// Checksum e firma della configurazione di sicurezza (§3.3).
/// <para>
/// Il checksum SHA-256 rileva la corruzione; la firma HMAC-SHA256 con chiave condivisa autentica
/// l'origine, impedendo che una configurazione arbitraria venga iniettata nel Safety Agent.
/// La chiave non è nel codice: arriva da <c>SAFETY_CONFIG_HMAC_KEY</c> (§6.6).
/// </para>
/// </summary>
public static class ConfigurationIntegrity
{
    /// <summary>
    /// Serializza in forma canonica. Determinismo indispensabile: lo stesso oggetto deve produrre
    /// gli stessi byte su processi e macchine diverse, altrimenti il checksum è inutilizzabile.
    /// </summary>
    public static string Canonicalize(SafetyConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, RuleSerialization.Options);
    }

    public static string ComputeChecksum(string canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return Convert.ToHexStringLower(hash);
    }

    public static byte[] ComputeSignature(string canonicalPayload, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (hmacKey.Length < 32)
            throw new ArgumentException(
                "La chiave HMAC deve essere di almeno 32 byte.", nameof(hmacKey));

        return HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(canonicalPayload));
    }

    /// <summary>
    /// Verifica checksum e firma. Il confronto della firma usa un algoritmo a tempo costante per
    /// non esporre informazioni tramite i tempi di risposta.
    /// </summary>
    public static IntegrityVerification Verify(string canonicalPayload, string expectedChecksum,
        byte[] expectedSignature, byte[] hmacKey)
    {
        var errors = new List<string>();

        var actualChecksum = ComputeChecksum(canonicalPayload);
        if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            errors.Add("Checksum SHA-256 non corrispondente: payload corrotto o alterato.");

        var actualSignature = ComputeSignature(canonicalPayload, hmacKey);
        if (!CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
            errors.Add("Firma HMAC non valida: origine della configurazione non autenticata.");

        return errors.Count == 0 ? IntegrityVerification.Ok() : new IntegrityVerification
        {
            IsValid = false,
            Errors = errors
        };
    }
}
