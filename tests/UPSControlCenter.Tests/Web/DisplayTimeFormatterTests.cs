using UPSControlCenter.Web.Services;

namespace UPSControlCenter.Tests.Web;

public sealed class DisplayTimeFormatterTests
{
    [Theory]
    [InlineData("2026-01-15T12:00:00+00:00", 13)]
    [InlineData("2026-07-15T12:00:00+00:00", 14)]
    public void EuropeRome_applica_automaticamente_ora_solare_e_legale(
        string utcValue,
        int expectedHour)
    {
        var formatter = new DisplayTimeFormatter("Europe/Rome");

        var local = formatter.Convert(DateTimeOffset.Parse(utcValue));

        Assert.Equal(expectedHour, local.Hour);
    }

    [Fact]
    public void Format_mantiene_esplicito_un_valore_assente()
    {
        var formatter = new DisplayTimeFormatter("Europe/Rome");

        Assert.Equal("mai", formatter.Format(null, "dd/MM/yyyy HH:mm:ss", "mai"));
    }

    [Theory]
    [InlineData("2026-01-15", "2026-01-14T23:00:00+00:00")] // ora solare: UTC+1
    [InlineData("2026-07-15", "2026-07-14T22:00:00+00:00")] // ora legale: UTC+2
    public void ToUtcStartOfDay_applica_il_fuso_dell_interfaccia(string date, string expectedUtc)
    {
        var formatter = new DisplayTimeFormatter("Europe/Rome");

        var result = formatter.ToUtcStartOfDay(DateOnly.Parse(date));

        Assert.Equal(DateTimeOffset.Parse(expectedUtc), result);
    }
}
