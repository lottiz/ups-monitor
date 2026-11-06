using UPSControlCenter.Contracts.Dto;
using UPSControlCenter.Web.Services;

namespace UPSControlCenter.Tests.Web;

public sealed class MeasurementSeriesSamplerTests
{
    [Fact]
    public void Conserva_l_ultima_misura_di_ogni_intervallo_di_venti_minuti()
    {
        var start = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var readings = new[]
        {
            Point(start.AddMinutes(1), 55),
            Point(start.AddMinutes(19), 54),
            Point(start.AddMinutes(20), 53),
            Point(start.AddMinutes(39), 52),
            Point(start.AddMinutes(40), 51)
        };

        var sampled = MeasurementSeriesSampler.Sample(readings, TimeSpan.FromMinutes(20));

        Assert.Equal(3, sampled.Count);
        Assert.Equal(new int?[] { 54, 52, 51 },
            sampled.Select(point => point.BatteryRuntimeSeconds));
    }

    [Fact]
    public void Ordina_i_campioni_anche_se_le_letture_non_sono_ordinate()
    {
        var start = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        var readings = new[]
        {
            Point(start.AddMinutes(41), 51),
            Point(start.AddMinutes(2), 55),
            Point(start.AddMinutes(21), 53)
        };

        var sampled = MeasurementSeriesSampler.Sample(readings, TimeSpan.FromMinutes(20));

        Assert.Equal(readings.OrderBy(point => point.RecordedAt).Select(point => point.RecordedAt),
            sampled.Select(point => point.RecordedAt));
    }

    private static UpsMeasurementHistoryDto Point(DateTimeOffset at, int runtime) => new()
    {
        RecordedAt = at,
        BatteryRuntimeSeconds = runtime
    };
}
