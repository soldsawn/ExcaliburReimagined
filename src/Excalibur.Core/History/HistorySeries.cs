using System.Text.Json.Serialization;
using Excalibur.Core.Fan;

namespace Excalibur.Core.History;

/// <summary>
/// History reduced for the wire: one point per <see cref="StepSeconds"/> from <see cref="From"/>,
/// null where nothing was recorded (sleep, service stopped, recording off). Short JSON names keep
/// a few thousand points small.
/// </summary>
public sealed record HistorySeries(DateTimeOffset From, int StepSeconds, IReadOnlyList<HistoryPoint?> Points)
{
    /// <summary>Buckets samples (sorted by time) into at most <paramref name="maxPoints"/> points over [from, to).</summary>
    public static HistorySeries Reduce(IEnumerable<HistorySample> samples, DateTime fromUtc, DateTime toUtc, int maxPoints)
    {
        double seconds = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        int step = (int)Math.Max(1, Math.Ceiling(seconds / Math.Max(1, maxPoints)));
        int count = (int)Math.Ceiling(seconds / step);
        var buckets = new List<HistorySample>?[count];
        foreach (var s in samples)
        {
            if (s.AtUtc < fromUtc || s.AtUtc >= toUtc) continue;
            int i = (int)((s.AtUtc - fromUtc).TotalSeconds / step);
            if (i >= 0 && i < count) (buckets[i] ??= []).Add(s);
        }
        var points = buckets.Select(b => b is null ? null : HistoryPoint.Of(b)).ToList();
        return new HistorySeries(new DateTimeOffset(fromUtc, TimeSpan.Zero), step, points);
    }
}

/// <summary>
/// One bucket: average and maximum temperatures (the maximum matters for curve tuning), average
/// RPM and applied percent, and the reasons of the last sample in the bucket.
/// </summary>
public sealed record HistoryPoint(
    [property: JsonPropertyName("c")] int CpuTempC,
    [property: JsonPropertyName("cm")] int CpuTempMax,
    [property: JsonPropertyName("g")] int GpuTempC,
    [property: JsonPropertyName("gm")] int GpuTempMax,
    [property: JsonPropertyName("cr")] int CpuRpm,
    [property: JsonPropertyName("gr")] int GpuRpm,
    [property: JsonPropertyName("ca")] int CpuApplied,
    [property: JsonPropertyName("ga")] int GpuApplied,
    [property: JsonPropertyName("cw")] FanReason CpuReason,
    [property: JsonPropertyName("gw")] FanReason GpuReason)
{
    public static HistoryPoint Of(IReadOnlyList<HistorySample> b)
    {
        int Avg(Func<HistorySample, int> f) => (int)Math.Round(b.Average(f), MidpointRounding.AwayFromZero);
        var last = b[^1];
        return new HistoryPoint(
            Avg(s => s.CpuTempC), b.Max(s => s.CpuTempC), Avg(s => s.GpuTempC), b.Max(s => s.GpuTempC),
            Avg(s => s.CpuRpm), Avg(s => s.GpuRpm), Avg(s => s.CpuApplied), Avg(s => s.GpuApplied),
            last.CpuReason, last.GpuReason);
    }
}
