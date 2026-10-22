namespace Excalibur.Core.History;

/// <summary>One chip over a stretch of history: hottest moment, average, and time spent at 80 °C or more.</summary>
public sealed record ChipSummary(int Max, int Average, TimeSpan Hot);

/// <summary>
/// The figures shown under the history chart. Hot time counts whole buckets whose average reached
/// <see cref="HotC"/>, so over long ranges (minutes per bucket) short spikes count less than they lasted.
/// </summary>
public sealed record HistorySummary(ChipSummary Cpu, ChipSummary Gpu)
{
    public const int HotC = 80;

    /// <summary>Null when the series holds no reading at all.</summary>
    public static HistorySummary? Of(HistorySeries series)
    {
        var points = series.Points.Where(p => p is not null).Select(p => p!).ToList();
        if (points.Count == 0) return null;
        var step = TimeSpan.FromSeconds(series.StepSeconds);
        ChipSummary Chip(Func<HistoryPoint, int> avg, Func<HistoryPoint, int> max) => new(
            points.Max(max),
            (int)Math.Round(points.Average(avg), MidpointRounding.AwayFromZero),
            step * points.Count(p => avg(p) >= HotC));
        return new HistorySummary(Chip(p => p.CpuTempC, p => p.CpuTempMax), Chip(p => p.GpuTempC, p => p.GpuTempMax));
    }
}
