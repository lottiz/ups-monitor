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
}
