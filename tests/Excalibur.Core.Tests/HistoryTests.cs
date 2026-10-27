using Excalibur.Core.Fan;
using Excalibur.Core.History;
using Excalibur.Core.Settings;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excalibur-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static HistorySample Sample(DateTime atUtc, int cpuC = 55, int gpuC = 50, int? cpuTarget = 40,
        FanReason cpuReason = FanReason.Curve, FanReason gpuReason = FanReason.Support) =>
        new(atUtc, cpuC, gpuC, 2400, 2100, cpuTarget, 35, 41, 36, cpuReason, gpuReason, FanLoopMode.Running,
            Boost: false, HoldsFans: true, Profile: ProfileCode.Of("Balanced"));

    private static DateTime Utc(int day, int h, int m, int s) => new(2026, 10, day, h, m, s, DateTimeKind.Utc);

    // ---- record format --------------------------------------------------------------------

    [Fact]
    public void A_sample_survives_its_18_bytes()
    {
        var s = Sample(Utc(20, 13, 45, 7), cpuC: 97, gpuC: 84, cpuTarget: null, cpuReason: FanReason.Hot, gpuReason: FanReason.Holding)
            with { CpuRpm = 5170, Mode = FanLoopMode.Faulted, Boost = true, HoldsFans = false, Profile = ProfileCode.Of("Manual") };
        var bytes = new byte[HistorySample.Size];
        s.Write(bytes);
        Assert.Equal(s, HistorySample.Read(bytes, s.AtUtc.Date));
    }

    [Fact]
    public void Status_without_a_reading_is_not_recorded()
    {
        var status = new FanStatus(FanLoopMode.Running, "Balanced", null, null, null, "P008B0", 0, false);
        Assert.Null(HistorySample.From(status, DateTime.UtcNow));
    }

    // ---- modes ----------------------------------------------------------------------------

    [Fact]
    public void Hour1_keeps_the_last_hour_in_memory_and_writes_nothing()
    {
        using var rec = new HistoryRecorder(_dir, HistoryMode.Hour1);
        var t0 = Utc(20, 10, 0, 0);
        for (int i = 0; i < HistoryRecorder.RingSeconds + 100; i++) rec.Record(Sample(t0.AddSeconds(i)));

        var all = rec.Samples(t0, t0.AddHours(2));
        Assert.Equal(HistoryRecorder.RingSeconds, all.Count);
        Assert.Equal(t0.AddSeconds(100), all[0].AtUtc);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Off_records_nothing_and_switching_off_forgets_the_ring()
    {
        using var rec = new HistoryRecorder(_dir, HistoryMode.Hour1);
        var t0 = Utc(20, 10, 0, 0);
        rec.Record(Sample(t0));
        rec.Mode = HistoryMode.Off;
        rec.Record(Sample(t0.AddSeconds(1)));
        Assert.Empty(rec.Samples(t0, t0.AddMinutes(1)));
    }

    [Fact]
    public void A_second_sample_in_the_same_second_is_skipped()
    {
        using var rec = new HistoryRecorder(_dir, HistoryMode.Hour1);
        var t0 = Utc(20, 10, 0, 0);
        rec.Record(Sample(t0, cpuC: 50));
        rec.Record(Sample(t0, cpuC: 60));
        Assert.Equal(50, Assert.Single(rec.Samples(t0, t0.AddSeconds(1))).CpuTempC);
    }

    // ---- disk -----------------------------------------------------------------------------

    [Fact]
    public void Days7_reads_older_samples_from_disk_and_recent_ones_from_memory()
    {
        var t0 = Utc(20, 8, 0, 0);
        int total = HistoryRecorder.RingSeconds + 600; // 10 minutes fall out of the ring
        using (var rec = new HistoryRecorder(_dir, HistoryMode.Days7))
        {
            for (int i = 0; i < total; i++) rec.Record(Sample(t0.AddSeconds(i), cpuC: 40 + i % 50));
            var all = rec.Samples(t0, t0.AddSeconds(total));
            Assert.Equal(total, all.Count);
            Assert.Equal(Enumerable.Range(0, total).Select(i => t0.AddSeconds(i)), all.Select(s => s.AtUtc));
            Assert.Equal(40 + 7 % 50, all[7].CpuTempC); // from the file
        }
        Assert.True(File.Exists(Path.Combine(_dir, "20261020.bin")));
    }

    [Fact]
    public void A_half_written_record_from_a_crash_is_cut_off()
    {
        // Today: a past day would be compressed by the maintenance the second recorder runs on start.
        var t0 = DateTime.UtcNow.Date;
        string file = Path.Combine(_dir, t0.ToString("yyyyMMdd") + ".bin");
        using (var rec = new HistoryRecorder(_dir, HistoryMode.Days7))
            for (int i = 0; i < 5; i++) rec.Record(Sample(t0.AddSeconds(i)));
        File.AppendAllText(file, "torn");

        using var again = new HistoryRecorder(_dir, HistoryMode.Days7);
        again.Record(Sample(t0.AddSeconds(5)));
        again.Flush();
        // Memory has only the new sample; ask for a range before it so the file is read.
        var fromDisk = again.Samples(t0, t0.AddSeconds(5));
        Assert.Equal(5, fromDisk.Count);
        Assert.Equal(0, (new FileInfo(file).Length - 8) % HistorySample.Size);
    }

    [Fact]
    public void Finished_days_are_compressed_and_old_days_deleted()
    {
        var today = DateTime.UtcNow.Date;
        var twoDaysAgo = today.AddDays(-2);
        using (var rec = new HistoryRecorder(_dir, HistoryMode.Days7))
            for (int i = 0; i < 10; i++) rec.Record(Sample(twoDaysAgo.AddHours(12).AddSeconds(i)));
        string stale = Path.Combine(_dir, today.AddDays(-9).ToString("yyyyMMdd") + ".bin.gz");
        File.WriteAllBytes(stale, [1, 2, 3]);

        using var again = new HistoryRecorder(_dir, HistoryMode.Days7); // maintenance runs on start

        string day = Path.Combine(_dir, twoDaysAgo.ToString("yyyyMMdd") + ".bin");
        Assert.False(File.Exists(day));
        Assert.True(File.Exists(day + ".gz"));
        Assert.False(File.Exists(stale));
        Assert.Equal(10, again.Samples(twoDaysAgo, today).Count);
    }

    // ---- reduction ------------------------------------------------------------------------

    [Fact]
    public void Reduce_averages_keeps_maxima_and_leaves_gaps_empty()
    {
        var t0 = Utc(20, 8, 0, 0);
        var samples = new[]
        {
            Sample(t0, cpuC: 50), Sample(t0.AddSeconds(1), cpuC: 60), Sample(t0.AddSeconds(2), cpuC: 70),
            // seconds 3-5: nothing (asleep)
            Sample(t0.AddSeconds(6), cpuC: 80, gpuReason: FanReason.Hot),
        };
        var series = HistorySeries.Reduce(samples, t0, t0.AddSeconds(9), maxPoints: 3);

        Assert.Equal(3, series.StepSeconds);
        Assert.Equal(3, series.Points.Count);
        Assert.Equal(60, series.Points[0]!.CpuTempC);
        Assert.Equal(70, series.Points[0]!.CpuTempMax);
        Assert.Null(series.Points[1]);
        Assert.Equal(FanReason.Hot, series.Points[2]!.GpuReason);
    }

    // ---- reasons --------------------------------------------------------------------------

    [Fact]
    public void A_cool_gpu_fan_follows_the_hot_cpu_as_support()
    {
        var c = new FanController();
        c.Decide(Snap(65, 45), Balanced, T0);
        Assert.Equal(FanReason.Curve, c.ReasonOf(FanId.Cpu));
        Assert.Equal(FanReason.Support, c.ReasonOf(FanId.Gpu));
    }

    [Fact]
    public void A_falling_temperature_is_reported_as_holding()
    {
        var c = new FanController();
        c.Decide(Snap(65, 65), Balanced, T0);
        c.Decide(Snap(55, 55), Balanced, T0.AddSeconds(1));
        Assert.Equal(FanReason.Holding, c.ReasonOf(FanId.Cpu));
    }

    [Fact]
    public void The_loop_reports_floors_hot_and_curve_percent()
    {
        // A flat 30% curve at 71 C; the floor from 70 C is 80%.
        var hw = new FakeFanHardware { CpuTempC = 71, GpuTempC = 50 };
        var loop = new FanControlLoop(hw, new FanLoopOptions { StartGrace = TimeSpan.Zero });
        var flat = new FanProfile("Flat", FanCurve.Flat(30), FanCurve.Flat(30));
        loop.Start(flat, T0);
        var s = loop.Tick(T0.AddSeconds(1));
        Assert.Equal(FanReason.SafetyFloor, s.CpuReason);
        Assert.Equal(30, s.CpuCurvePercent);

        hw.CpuTempC = 95;
        loop.Tick(T0.AddSeconds(2));
        s = loop.Tick(T0.AddSeconds(3));
        Assert.Equal(FanReason.Hot, s.CpuReason);
        Assert.Equal(FanReason.Hot, s.GpuReason);
    }
}
