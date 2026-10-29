using System.Security.Cryptography;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Safety;

namespace UPSControlCenter.Tests.Safety;

/// <summary>Integrità e autenticità della configurazione distribuita al Safety Agent (§3.3).</summary>
public sealed class ConfigurationIntegrityTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(48);

    internal static SafetyConfiguration Minimal(long version = 1) => new()
    {
        Version = version,
        GeneratedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ExecutionMode = ExecutionMode.DryRun,
        Ups = new SafetyUpsConfig
        {
            NutHost = "127.0.0.1",
            NutPort = 3493,
            NutUpsName = "riello",
            PollingIntervalSeconds = 10,
            StaleDataThresholdSeconds = 60
        },
        Thresholds = new SafetyThresholds()
    };

    [Fact]
    public void La_serializzazione_canonica_e_deterministica()
    {
        // Senza determinismo il checksum sarebbe inutilizzabile fra processi diversi.
        var first = ConfigurationIntegrity.Canonicalize(Minimal());
        var second = ConfigurationIntegrity.Canonicalize(Minimal());

        Assert.Equal(first, second);
        Assert.Equal(ConfigurationIntegrity.ComputeChecksum(first),
            ConfigurationIntegrity.ComputeChecksum(second));
    }

    [Fact]
    public void Checksum_e_firma_valide_superano_la_verifica()
    {
        var payload = ConfigurationIntegrity.Canonicalize(Minimal());
        var checksum = ConfigurationIntegrity.ComputeChecksum(payload);
        var signature = ConfigurationIntegrity.ComputeSignature(payload, Key);

        var result = ConfigurationIntegrity.Verify(payload, checksum, signature, Key);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Un_payload_alterato_viene_respinto()
    {
        var payload = ConfigurationIntegrity.Canonicalize(Minimal());
        var checksum = ConfigurationIntegrity.ComputeChecksum(payload);
        var signature = ConfigurationIntegrity.ComputeSignature(payload, Key);

        // Manomissione: la soglia di spegnimento viene abbassata a 1 secondo.
        var tampered = payload.Replace("\"maxBlackoutDurationSeconds\":1800",
            "\"maxBlackoutDurationSeconds\":1");

        Assert.NotEqual(payload, tampered);

        var result = ConfigurationIntegrity.Verify(tampered, checksum, signature, Key);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count); // checksum e firma entrambi non corrispondono
    }

    [Fact]
    public void Una_firma_prodotta_con_chiave_diversa_viene_respinta()
    {
        var payload = ConfigurationIntegrity.Canonicalize(Minimal());
        var checksum = ConfigurationIntegrity.ComputeChecksum(payload);

        var attackerKey = RandomNumberGenerator.GetBytes(48);
        var forged = ConfigurationIntegrity.ComputeSignature(payload, attackerKey);

        var result = ConfigurationIntegrity.Verify(payload, checksum, forged, Key);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Firma HMAC non valida"));
    }

    [Fact]
    public void Una_chiave_troppo_corta_viene_rifiutata()
    {
        var payload = ConfigurationIntegrity.Canonicalize(Minimal());

        var ex = Assert.Throws<ArgumentException>(
            () => ConfigurationIntegrity.ComputeSignature(payload, new byte[16]));

        Assert.Contains("32 byte", ex.Message);
    }

    [Fact]
    public void Configurazioni_diverse_producono_checksum_diversi()
    {
        var a = ConfigurationIntegrity.ComputeChecksum(
            ConfigurationIntegrity.Canonicalize(Minimal(1)));
        var b = ConfigurationIntegrity.ComputeChecksum(
            ConfigurationIntegrity.Canonicalize(Minimal(2)));

        Assert.NotEqual(a, b);
    }
}
