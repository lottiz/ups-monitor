using UPSControlCenter.Infrastructure.Security;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>Protezione fittizia (base64, non cifratura reale) per isolare i test dalla Data Protection di ASP.NET Core.</summary>
internal sealed class FakeCredentialProtector : ICredentialProtector
{
    public string CurrentKeyId => "fake";
    public bool FailToUnprotect { get; set; }

    public byte[] Protect(string plaintext, string purpose) =>
        System.Text.Encoding.UTF8.GetBytes(plaintext);

    public string? Unprotect(byte[] ciphertext, string purpose) =>
        FailToUnprotect ? null : System.Text.Encoding.UTF8.GetString(ciphertext);
}
