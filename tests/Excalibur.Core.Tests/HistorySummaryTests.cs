using Excalibur.Core.Fan;
using Excalibur.Core.History;
using Xunit;

namespace Excalibur.Core.Tests;

public class HistorySummaryTests
{
    private static HistoryPoint P(int cpu, int cpuMax, int gpu, int gpuMax) =>
        new(cpu, cpuMax, gpu, gpuMax, 2400, 2100, 40, 35, FanReason.Curve, FanReason.Curve);

    private static HistorySeries Series(int step, params HistoryPoint?[] points) =>
        new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), step, points);

    [Fact]
    public void Peaks_come_from_the_bucket_maxima_and_averages_skip_gaps()
    {
        var s = HistorySummary.Of(Series(10, P(50, 61, 40, 44), null, P(70, 88, 60, 67)));
        Assert.Equal(new ChipSummary(88, 60, TimeSpan.Zero), s!.Cpu);
        Assert.Equal(new ChipSummary(67, 50, TimeSpan.Zero), s.Gpu);
    }

    [Fact]
    public void Hot_time_counts_buckets_averaging_80_or_more()
    {
        var s = HistorySummary.Of(Series(30, P(79, 95, 50, 50), P(80, 84, 50, 50), P(91, 93, 82, 85)));
        Assert.Equal(TimeSpan.FromSeconds(60), s!.Cpu.Hot);
        Assert.Equal(TimeSpan.FromSeconds(30), s.Gpu.Hot);
    }

    [Fact]
    public void Nothing_recorded_gives_no_summary()
        => Assert.Null(HistorySummary.Of(Series(10, null, null)));
}
