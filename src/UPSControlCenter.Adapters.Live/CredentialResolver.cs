using UPSControlCenter.Core.Entities;
using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Adapters.Live;

/// <summary>Credenziali decifrate di un dispositivo, valide solo per la durata della chiamata.</summary>
public sealed record ResolvedCredential
{
    public required string Username { get; init; }
    public required string Secret { get; init; }
    public required Core.Enums.CredentialAuthType AuthType { get; init; }
    public string? Fingerprint { get; init; }
}

/// <summary>
/// Decifra il segreto del profilo di connessione di un dispositivo al momento dell'uso.
/// <para>
/// Il segreto non viene mai conservato in chiaro più del necessario: questo metodo lo decifra,
/// lo restituisce per la singola operazione in corso, e non lo registra in nessun log (§8.4).
/// </para>
/// </summary>
public static class CredentialResolver
{
    public static ResolvedCredential? Resolve(ManagedDevice device, ICredentialProtector protector)
    {
        var profile = device.ConnectionProfile;
        if (profile is null || !profile.IsEnabled) return null;
        if (profile.SecretCiphertext is not { Length: > 0 }) return null;

        var secret = protector.Unprotect(profile.SecretCiphertext, ProtectionPurposes.ConnectionProfileSecret);
        if (secret is null) return null;

        return new ResolvedCredential
        {
            Username = profile.Username,
            Secret = secret,
            AuthType = profile.AuthType,
            Fingerprint = profile.Fingerprint
        };
    }
}
