using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Tests.Rules;

public sealed class StatusParserTests
{
    [Theory]
    [InlineData("OL", UpsStatusFlags.OnLine)]
    [InlineData("OB", UpsStatusFlags.OnBattery)]
    [InlineData("OB LB", UpsStatusFlags.OnBattery | UpsStatusFlags.LowBattery)]
    [InlineData("OL CHRG", UpsStatusFlags.OnLine | UpsStatusFlags.Charging)]
    [InlineData("OB DISCHRG LB", UpsStatusFlags.OnBattery | UpsStatusFlags.Discharging | UpsStatusFlags.LowBattery)]
    public void Parse_riconosce_le_combinazioni_di_stato(string raw, UpsStatusFlags expected)
    {
        Assert.Equal(expected, UpsStatusParser.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_senza_valore_restituisce_Unknown_e_non_OnLine(string? raw)
    {
        var result = UpsStatusParser.Parse(raw);

        // Un valore assente non deve mai essere interpretato come "alimentazione presente":
        // sarebbe l'errore più pericoloso possibile in questo sistema.
        Assert.True(result.IsUnknown());
        Assert.False(result.IsOnLine());
    }

    [Fact]
    public void Parse_ignora_i_token_non_riconosciuti_senza_errori()
    {
        // I driver NUT aggiungono token propri: non devono far fallire il parsing.
        var result = UpsStatusParser.Parse("OB TOKENSCONOSCIUTO DISCHRG");

        Assert.True(result.IsOnBattery());
        Assert.True(result.HasFlag(UpsStatusFlags.Discharging));
    }

    [Fact]
    public void Parse_con_soli_token_sconosciuti_restituisce_Unknown()
    {
        Assert.True(UpsStatusParser.Parse("XYZ QWE").IsUnknown());
    }

    [Fact]
    public void OnBattery_prevale_su_OnLine_durante_le_transizioni()
    {
        // Alcuni driver riportano entrambi i flag: il caso pericoloso è quello su batteria,
        // quindi è quello che deve vincere.
        var result = UpsStatusParser.Parse("OL OB");

        Assert.True(result.IsOnBattery());
        Assert.False(result.IsOnLine());
    }

    [Fact]
    public void ToNutString_rappresenta_la_bitmask_in_forma_leggibile()
    {
        var flags = UpsStatusFlags.OnBattery | UpsStatusFlags.LowBattery;
        Assert.Equal("OB LB", flags.ToNutString());
    }
}
