using UPSControlCenter.Contracts.Dto;

namespace UPSControlCenter.Web.Services;

/// <summary>
/// Riduce le letture ad alta frequenza a una serie adatta al browser, conservando l'ultima
/// misura di ogni intervallo. Il polling operativo non viene rallentato.
/// </summary>
public static class MeasurementSeriesSampler
{
    public static IReadOnlyList<UpsMeasurementHistoryDto> Sample(
        IEnumerable<UpsMeasurementHistoryDto> measurements,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var intervalSeconds = checked((long)interval.TotalSeconds);
        var buckets = new Dictionary<long, UpsMeasurementHistoryDto>();

        foreach (var measurement in measurements)
        {
            var bucket = measurement.RecordedAt.ToUnixTimeSeconds() / intervalSeconds;
            if (!buckets.TryGetValue(bucket, out var current)
                || measurement.RecordedAt > current.RecordedAt)
            {
                buckets[bucket] = measurement;
            }
        }

        return buckets.Values.OrderBy(m => m.RecordedAt).ToArray();
    }
}
