using Excalibur.Core.Fan;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class FanControllerTests
{
    private readonly FanController _c = new();

    [Fact]
    public void Idle_balanced_runs_both_fans_at_the_curve_floor()
    {
        var d = _c.Decide(Snap(50, 45), Balanced, T0);
        Assert.Equal(P(30), d.Cpu);
        Assert.Equal(P(30), d.Gpu);
    }

    [Fact]
    public void Hot_cpu_drags_the_gpu_fan_up_to_its_curve_minus_the_support_offset()
    {
        var d = _c.Decide(Snap(75, 45), Balanced, T0);
        Assert.Equal(P(100), d.Cpu);
        Assert.Equal(P(100 - FanController.SupportOffset), d.Gpu);
    }

    [Fact]
    public void A_rise_is_applied_once_it_has_lasted_the_rise_delay()
    {
        _c.Decide(Snap(50, 45), Balanced, T0);
        for (int t = 1; t <= 3; t++)
            Assert.Equal(P(30), _c.Decide(Snap(65, 45), Balanced, T0.AddSeconds(t)).Cpu);
        Assert.Equal(P(85), _c.Decide(Snap(65, 45), Balanced, T0.AddSeconds(4)).Cpu);
    }

    [Fact]
    public void A_short_burst_does_not_raise_the_fan()
    {
        // 2026-10-23 07:39:53-55, Balanced: 66 C for a second or two between 55-58 C took the fan from 52% to 80%.
        int start = _c.Decide(Snap(57, 45), Balanced, T0).Cpu.Percent;
        int[] temps = [66, 66, 55, 57, 62, 58, 56];
        for (int t = 0; t < temps.Length; t++)
            Assert.True(_c.Decide(Snap(temps[t], 45), Balanced, T0.AddSeconds(t + 1)).Cpu.Percent <= start);
    }

    [Fact]
    public void A_rise_goes_only_as_far_as_the_lowest_demand_of_the_delay()
    {
        _c.Decide(Snap(50, 45), Balanced, T0);
        _c.Decide(Snap(60, 45), Balanced, T0.AddSeconds(1));
        _c.Decide(Snap(65, 45), Balanced, T0.AddSeconds(2));
        _c.Decide(Snap(65, 45), Balanced, T0.AddSeconds(3));
        Assert.Equal(P(60), _c.Decide(Snap(65, 45), Balanced, T0.AddSeconds(4)).Cpu);
    }

    [Fact]
    public void Small_drop_inside_the_hysteresis_band_keeps_the_fan()
    {
        _c.Decide(Snap(65, 45), Balanced, T0);
        var d = _c.Decide(Snap(63, 45), Balanced, T0.AddSeconds(5));
        Assert.Equal(P(85), d.Cpu);
    }

    [Fact]
    public void A_dip_shorter_than_the_fall_delay_keeps_the_fan()
    {
        // 2026-10-23 11:44, Balanced under load: 72-75 C with 3-5 s dips to 62-64 C took the fan from 97% to 82% and back.
        _c.Decide(Snap(75, 45), Balanced, T0);
        int[] temps = [64, 62, 62, 64, 66, 75, 74, 63, 62, 64];
        for (int t = 0; t < temps.Length; t++)
            Assert.Equal(P(100), _c.Decide(Snap(temps[t], 45), Balanced, T0.AddSeconds(t + 1)).Cpu);
    }

    [Fact]
    public void Large_drop_ramps_down_no_faster_than_the_slew_limit_once_it_has_lasted_the_fall_delay()
    {
        _c.Decide(Snap(75, 45), Balanced, T0);
        int fall = (int)FanController.FallDelay.TotalSeconds;
        FanDecision d = default;
        for (int i = 1; i <= fall; i++)
            Assert.Equal(P(100), (d = _c.Decide(Snap(50, 45), Balanced, T0.AddSeconds(i))).Cpu);
        d = _c.Decide(Snap(50, 45), Balanced, T0.AddSeconds(fall + 1));
        Assert.Equal(P(98), d.Cpu); // 100 - 2.5, rounded

        for (int i = fall + 2; i <= 80; i++) d = _c.Decide(Snap(50, 45), Balanced, T0.AddSeconds(i));
        // Settles at the curve read HysteresisC hotter: Balanced(53) = 36.
        Assert.Equal(P(Balanced.CpuCurve.Evaluate(50 + FanController.HysteresisC)), d.Cpu);
    }

    [Fact]
    public void Manual_values_are_taken_literally_without_support()
    {
        var d = _c.Decide(Snap(50, 46), FanProfile.Manual(70, 55), T0);
        Assert.Equal(P(70), d.Cpu);
        Assert.Equal(P(55), d.Gpu);
    }

    [Fact]
    public void Cooling_boost_means_both_fans_at_100()
    {
        var d = _c.Decide(Snap(45, 45, boost: true), Quiet, T0);
        Assert.Equal(P(100), d.Cpu);
        Assert.Equal(P(100), d.Gpu);
    }

    [Fact]
    public void Ec_auto_profile_hands_both_fans_to_the_ec()
        => Assert.Equal(FanDecision.BothAuto, _c.Decide(Snap(90, 90), FanProfile.EcAuto, T0));
}

public class SafetyPolicyTests
{
    [Fact]
    public void Never_below_the_absolute_floor()
    {
        var d = SafetyPolicy.Apply(Snap(45, 45), new FanDecision(P(10), P(0)), hot: false, out var reason);
        Assert.Equal(P(SafetyPolicy.AbsoluteFloor), d.Cpu);
        Assert.Equal(P(SafetyPolicy.AbsoluteFloor), d.Gpu);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(69, 30)]
    [InlineData(71, 80)]
    [InlineData(79, 90)]
    public void Temperature_floors_raise_a_slow_fan(int cpuC, int expectedMin)
    {
        var d = SafetyPolicy.Apply(Snap(cpuC, 45), new FanDecision(P(30), P(30)), hot: false, out _);
        Assert.Equal(P(expectedMin), d.Cpu);
    }

    [Fact]
    public void Hot_puts_both_fans_at_100()
    {
        var d = SafetyPolicy.Apply(Snap(50, HotTracker.HotC), new FanDecision(P(30), P(30)), hot: true, out _);
        Assert.Equal(P(100), d.Cpu);
        Assert.Equal(P(100), d.Gpu);
    }

    [Fact]
    public void Hot_logs_the_raw_temperature_it_decided_on()
    {
        SafetyPolicy.Apply(Snap(60, 46), new FanDecision(P(80), P(70)), hot: true, out var reason, out _, out _, raw: Snap(97, 46));
        Assert.Contains("CPU 97C", reason);
    }

    [Fact]
    public void Never_lowers_a_fan()
    {
        var d = SafetyPolicy.Apply(Snap(45, 45), new FanDecision(P(100), P(70)), hot: false, out var reason);
        Assert.Equal(new FanDecision(P(100), P(70)), d);
        Assert.Null(reason);
    }

    [Fact]
    public void Auto_passes_through()
        => Assert.Equal(FanDecision.BothAuto, SafetyPolicy.Apply(Snap(95, 95), FanDecision.BothAuto, hot: true, out _));
}

public class HotTrackerTests
{
    [Fact]
    public void A_short_spike_is_not_hot()
    {
        var h = new HotTracker();
        Assert.False(h.Update(Snap(89, 50), T0));
        Assert.False(h.Update(Snap(89, 50), T0.AddSeconds(1)));
        Assert.False(h.Update(Snap(55, 50), T0.AddSeconds(2)));
        Assert.False(h.Update(Snap(89, 50), T0.AddSeconds(3))); // the count restarted
    }

    [Fact]
    public void Three_seconds_at_80C_is_hot()
    {
        var h = new HotTracker();
        Assert.False(h.Update(Snap(80, 50), T0));
        Assert.False(h.Update(Snap(81, 50), T0.AddSeconds(1)));
        Assert.True(h.Update(Snap(82, 50), T0.AddSeconds(2.6)));
    }

    [Fact]
    public void Immediate_threshold_twice_in_a_row_is_hot()
    {
        var h = new HotTracker();
        Assert.False(h.Update(Snap(50, HotTracker.ImmediateC), T0));
        Assert.True(h.Update(Snap(50, HotTracker.ImmediateC), T0.AddSeconds(1)));
    }

    [Fact]
    public void A_single_reading_at_the_immediate_threshold_is_not_hot()
    {
        // 2026-10-23 07:24:04-08, MSR package temperature: 78, 55, 100, 82, 51.
        var h = new HotTracker();
        Assert.False(h.Update(Snap(78, 49), T0));
        Assert.False(h.Update(Snap(55, 49), T0.AddSeconds(1)));
        Assert.False(h.Update(Snap(100, 49), T0.AddSeconds(2)));
        Assert.False(h.Update(Snap(82, 49), T0.AddSeconds(3)));
        Assert.False(h.Update(Snap(51, 49), T0.AddSeconds(4)));
    }
}

public class FloorTrackerTests
{
    private static int FloorFor(FloorTracker f, int cpuC, double second)
    {
        var d = SafetyPolicy.Apply(f.Apply(Snap(cpuC, 50), T0.AddSeconds(second)), new FanDecision(P(60), P(60)), hot: false, out _);
        return d.Cpu.Percent;
    }

    [Fact]
    public void A_two_second_spike_over_70C_leaves_the_fan_on_its_curve()
    {
        // 2026-10-24 18:30:23: 60 C, a 2 s boost spike, 60 C again.
        var f = new FloorTracker();
        Assert.Equal(60, FloorFor(f, 60, 0));
        Assert.Equal(60, FloorFor(f, 71, 1));
        Assert.Equal(60, FloorFor(f, 72, 2));
        Assert.Equal(60, FloorFor(f, 60, 3));
    }

    [Fact]
    public void Three_seconds_over_70C_raises_the_floor_and_a_fall_releases_it()
    {
        var f = new FloorTracker();
        FloorFor(f, 60, 0);
        Assert.Equal(60, FloorFor(f, 71, 1));
        Assert.Equal(60, FloorFor(f, 71, 2));
        Assert.Equal(60, FloorFor(f, 72, 3));
        Assert.Equal(80, FloorFor(f, 72, 4));
        Assert.Equal(60, FloorFor(f, 62, 5));
    }

    [Fact]
    public void The_78C_floor_waits_the_same_way()
    {
        var f = new FloorTracker();
        FloorFor(f, 72, 0);
        Assert.Equal(80, FloorFor(f, 79, 1));
        Assert.Equal(80, FloorFor(f, 79, 2));
        Assert.Equal(80, FloorFor(f, 79, 3));
        Assert.Equal(90, FloorFor(f, 79, 4));
    }
}

public class TemperatureFilterTests
{
    [Fact]
    public void Rise_is_limited_and_fall_is_immediate()
    {
        var f = new TemperatureFilter();
        Assert.Equal(55, f.Apply(Snap(55, 45), T0).CpuTempC);
        Assert.Equal(58, f.Apply(Snap(89, 45), T0.AddSeconds(1)).CpuTempC);
        Assert.Equal(61, f.Apply(Snap(89, 45), T0.AddSeconds(2)).CpuTempC);
        Assert.Equal(55, f.Apply(Snap(55, 45), T0.AddSeconds(3)).CpuTempC);
    }

    [Fact]
    public void First_reading_after_reset_is_taken_as_is()
    {
        var f = new TemperatureFilter();
        f.Apply(Snap(50, 45), T0);
        f.Reset();
        Assert.Equal(85, f.Apply(Snap(85, 45), T0.AddSeconds(1)).CpuTempC);
    }
}

public class BuiltInCurveTests
{
    [Fact]
    public void No_built_in_curve_sits_below_a_safety_floor()
    {
        // A curve under a floor makes the fan hunt between the two (Quiet at 65 C until 2026-10-23).
        foreach (var profile in FanProfile.BuiltIn.Where(p => !p.IsEcAuto))
            for (int c = 30; c <= 100; c++)
            {
                int percent = profile.CpuCurve.Evaluate(c);
                SafetyPolicy.Apply(Snap(c, c), new FanDecision(P(percent), P(percent)), hot: false, out string? reason);
                Assert.True(reason is null, $"{profile.Name} at {c} C: {reason}");
            }
    }
}
