using Excalibur.Core.Fan;
using Xunit.Abstractions;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class FanControlLoopTests
{
    private readonly ITestOutputHelper _output;
    private readonly FakeFanHardware _hw = new();
    private readonly FanControlLoop _loop;

    public FanControlLoopTests(ITestOutputHelper output)
    {
        _output = output;
        _loop = new FanControlLoop(_hw);
    }

    private FanStatus TickAt(int second) => _loop.Tick(T0.AddSeconds(second));

    [Fact]
    public void Start_hands_fans_to_the_ec_first_and_runs_on_a_known_ec()
    {
        _loop.Start(FanProfile.Balanced, T0);
        Assert.Equal([(FanId.Cpu, FanTarget.Auto), (FanId.Gpu, FanTarget.Auto)], _hw.Writes);
        Assert.Equal(FanLoopMode.Running, _loop.Status.Mode);
    }

    [Fact]
    public void Balanced_tick_writes_the_curve()
    {
        _loop.Start(FanProfile.Balanced, T0);
        var s = TickAt(1);
        Assert.Equal(P(30), _hw.CpuTarget);
        Assert.Equal(P(30), _hw.GpuTarget);
        Assert.True(s.HoldsFans);
    }

    [Fact]
    public void Curve_changes_are_written_at_most_every_5_s_per_fan()
    {
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
        var writeTicks = new List<int>();
        for (int i = 2; i <= 40; i++)
        {
            _hw.CpuTempC = Math.Min(48 + i / 2.0, 66); // a slow climb: the curve wants a little more every tick
            int before = _hw.Writes.Count(w => w.Fan == FanId.Cpu);
            TickAt(i);
            if (_hw.Writes.Count(w => w.Fan == FanId.Cpu) > before) writeTicks.Add(i);
        }
        _output.WriteLine(string.Join(", ", writeTicks));
        Assert.True(writeTicks.Count >= 3);
        Assert.True(writeTicks[0] >= 6, "the first change waits 5 s after the write at second 1");
        Assert.All(writeTicks.Zip(writeTicks.Skip(1)), p => Assert.True(p.Second - p.First >= 5));
    }

    [Fact]
    public void A_users_change_is_written_at_once()
    {
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
        _loop.SetManual(60, 61, TimeSpan.FromMinutes(1), T0.AddSeconds(2));
        TickAt(2);
        Assert.Equal(P(60), _hw.CpuTarget);
        Assert.Equal(P(61), _hw.GpuTarget);
    }

    [Fact]
    public void Unknown_ec_firmware_is_monitor_only_and_never_writes_a_percent()
    {
        _hw.EcVersion = "P010A0";
        _loop.Start(FanProfile.Balanced, T0);
        for (int i = 1; i <= 30; i++) TickAt(i);
        Assert.Equal(FanLoopMode.MonitorOnly, _loop.Status.Mode);
        Assert.All(_hw.Writes, w => Assert.True(w.Target.IsAuto));
    }

    [Fact]
    public void Repeated_read_failures_fault_and_hand_fans_to_the_ec()
    {
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
        Assert.Equal(P(30), _hw.CpuTarget);

        _hw.FailNextReads = 3;
        for (int i = 2; i <= 4; i++) TickAt(i);

        Assert.Equal(FanLoopMode.Faulted, _loop.Status.Mode);
        Assert.True(_hw.CpuTarget.IsAuto);
        Assert.True(_hw.GpuTarget.IsAuto);
        Assert.False(_loop.Status.HoldsFans);

        int writes = _hw.Writes.Count;
        for (int i = 5; i <= 20; i++) TickAt(i);
        Assert.Equal(writes, _hw.Writes.Count); // stays hands-off while faulted

        _loop.ClearFault(T0.AddSeconds(21));
        TickAt(22);
        Assert.Equal(P(30), _hw.CpuTarget);
    }

    [Fact]
    public void Two_read_failures_do_not_fault()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.FailNextReads = 2;
        for (int i = 1; i <= 3; i++) TickAt(i);
        Assert.Equal(FanLoopMode.Running, _loop.Status.Mode);
    }

    [Fact]
    public void Repeated_write_failures_fault()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.FailNextWrites = 2; // CPU write fails, then GPU write fails, in the same tick
        TickAt(1);
        Assert.Equal(FanLoopMode.Faulted, _loop.Status.Mode);
        Assert.True(_hw.CpuTarget.IsAuto);
    }

    [Fact]
    public void Stop_hands_fans_to_the_ec()
    {
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
        _loop.Stop(T0.AddSeconds(2));
        Assert.True(_hw.CpuTarget.IsAuto);
        Assert.True(_hw.GpuTarget.IsAuto);
        Assert.Equal(FanLoopMode.Stopped, _loop.Status.Mode);
    }

    [Fact]
    public void Tiny_changes_are_not_written()
    {
        _loop.Start(FanProfile.Balanced, T0);
        TickAt(1);
        int writes = _hw.Writes.Count;
        _hw.CpuTempC = 50.4; // still 30%
        TickAt(2);
        Assert.Equal(writes, _hw.Writes.Count);
    }

    [Fact]
    public void Manual_mode_expires_back_to_the_previous_profile()
    {
        _loop.Start(FanProfile.Quiet, T0);
        _loop.SetManual(70, 60, TimeSpan.FromMinutes(1), T0);
        TickAt(1);
        Assert.Equal(P(70), _hw.CpuTarget);
        Assert.Equal(P(60), _hw.GpuTarget);

        var s = TickAt(61);
        Assert.Equal("Quiet", s.Profile);
        Assert.Null(s.ManualUntil);
    }

    [Fact]
    public void Manual_mode_is_capped_at_ten_minutes()
    {
        _loop.Start(FanProfile.Balanced, T0);
        Assert.Throws<ArgumentOutOfRangeException>(() => _loop.SetManual(50, 50, TimeSpan.FromMinutes(11), T0));
    }

    [Fact]
    public void Manual_mode_is_still_subject_to_the_safety_floors()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.CpuTempC = 85;
        _loop.SetManual(25, 25, TimeSpan.FromMinutes(1), T0);
        TickAt(1);
        Assert.Equal(P(90), _hw.CpuTarget);   // the 72 C floor, at once
        for (int i = 2; i <= 4; i++) TickAt(i);
        Assert.Equal(P(100), _hw.CpuTarget);  // hot after 3 s
        Assert.Equal(P(100), _hw.GpuTarget);
    }

    [Fact]
    public void A_held_safety_override_is_logged_once_when_it_engages_and_once_when_it_releases()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.CpuTempC = 89;
        for (int i = 1; i <= 20; i++) TickAt(i);
        _hw.CpuTempC = 50;
        _hw.GpuTempC = 45;
        for (int i = 21; i <= 80; i++) TickAt(i);

        var safety = _loop.Events.Where(e => e.Message.StartsWith("safety")).Select(e => e.Message).ToList();
        Assert.Equal(2, safety.Count);
        Assert.Equal("safety: released", safety[1]);
    }

    [Fact]
    public void A_two_second_boost_spike_does_not_slam_the_fans()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.CpuTempC = 55;
        for (int i = 1; i <= 30; i++) TickAt(i);
        int before = _hw.CpuTarget.Percent;

        _hw.CpuTempC = 89;
        TickAt(31);
        TickAt(32);
        Assert.InRange(_hw.CpuTarget.Percent, before, 70);
        Assert.True(_hw.GpuTarget.Percent < 100);

        _hw.CpuTempC = 55;
        TickAt(33);
        Assert.DoesNotContain(_loop.Events, e => e.Message.Contains("hot"));
    }

    [Fact]
    public void Sustained_heat_still_reaches_100_within_a_few_seconds()
    {
        _loop.Start(FanProfile.Balanced, T0);
        _hw.CpuTempC = 55;
        for (int i = 1; i <= 30; i++) TickAt(i);
        _hw.CpuTempC = 89;
        for (int i = 31; i <= 34; i++) TickAt(i);
        Assert.Equal(P(100), _hw.CpuTarget);
        Assert.Equal(P(100), _hw.GpuTarget);
    }

    [Fact]
    public void Balanced_is_quieter_at_idle_and_never_hotter_under_load_than_stock()
    {
        var (stockMax, stockIdleFan) = Simulate(FanProfile.EcAuto);
        var (ourMax, ourIdleFan) = Simulate(FanProfile.Balanced);
        _output.WriteLine($"stock: idle CPU fan {stockIdleFan}%, peak CPU {stockMax:F1}C");
        _output.WriteLine($"Balanced: idle CPU fan {ourIdleFan}%, peak CPU {ourMax:F1}C");

        // The slower fan lets the idle CPU warm to ~55 C, where the curve asks for ~40%,
        // so the idle gain is about a quarter, not the 66 -> 30 the curve alone suggests.
        Assert.True(ourIdleFan <= stockIdleFan - 15, $"idle fan {ourIdleFan}% vs stock {stockIdleFan}%");
        Assert.True(ourMax <= stockMax + 0.5, $"peak {ourMax:F1}C vs stock {stockMax:F1}C");
    }

    /// <summary>10 simulated minutes: 2 idle, 4 CPU load, 3 GPU load, 1 idle. Returns peak CPU temperature and idle CPU fan %.</summary>
    private static (double PeakCpuC, int IdleCpuFan) Simulate(FanProfile profile)
    {
        var hw = new FakeFanHardware();
        var loop = new FanControlLoop(hw);
        loop.Start(profile, T0);
        double peak = 0;
        int idleFan = 0;
        for (int s = 1; s <= 600; s++)
        {
            (hw.CpuLoad, hw.GpuLoad) = s switch
            {
                <= 120 => (0.0, 0.0),
                <= 360 => (1.0, 0.1),
                <= 540 => (0.5, 1.0),
                _ => (0.0, 0.0),
            };
            hw.Advance(TimeSpan.FromSeconds(1));
            loop.Tick(T0.AddSeconds(s));
            peak = Math.Max(peak, hw.CpuTempC);
            if (s == 120) idleFan = hw.AppliedPercent(FanId.Cpu);
        }
        return (peak, idleFan);
    }

    [Fact]
    public void Start_logs_the_hand_over_to_the_ec()
    {
        _loop.Start(FanProfile.Balanced, T0);
        Assert.Contains(_loop.Events, e => e.Message == "fans handed to the EC (start)");
    }

    [Fact]
    public async Task Status_and_events_do_not_wait_for_a_slow_ec_read()
    {
        var slow = new SlowReadHardware(_hw);
        var loop = new FanControlLoop(slow);
        loop.Start(FanProfile.Balanced, T0);
        loop.Tick(T0.AddSeconds(1));

        slow.Block = new ManualResetEventSlim(false);
        var tick = Task.Run(() => loop.Tick(T0.AddSeconds(2)));
        Assert.True(await Task.Run(() => slow.Entered.Wait(TimeSpan.FromSeconds(5))), "tick never reached the EC read");

        var query = Task.Run(() => (loop.Status, loop.Events, loop.TotalEvents));
        bool answered = await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(2))) == query;
        slow.Block.Set();
        await tick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(answered, "Status/Events blocked behind the tick");
    }

    private sealed class SlowReadHardware(IFanHardware inner) : IFanHardware
    {
        public ManualResetEventSlim? Block;
        public readonly ManualResetEventSlim Entered = new(false);

        public FanSnapshot Read()
        {
            if (Block is { } block)
            {
                Entered.Set();
                block.Wait(TimeSpan.FromSeconds(10));
            }
            return inner.Read();
        }

        public void SetTarget(FanId fan, FanTarget target) => inner.SetTarget(fan, target);
        public string? ReadEcVersion() => inner.ReadEcVersion();
    }
}
