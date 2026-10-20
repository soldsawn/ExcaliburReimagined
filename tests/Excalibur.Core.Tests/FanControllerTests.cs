using Excalibur.Core.Fan;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class FanControllerTests
{
    private readonly FanController _c = new();

    [Fact]
    public void Idle_balanced_runs_both_fans_at_the_curve_floor()
    {
        var d = _c.Decide(Snap(50, 45), FanProfile.Balanced, T0);
        Assert.Equal(P(30), d.Cpu);
        Assert.Equal(P(30), d.Gpu);
    }

    [Fact]
    public void Hot_cpu_drags_the_gpu_fan_up_to_its_curve_minus_the_support_offset()
    {
        var d = _c.Decide(Snap(75, 45), FanProfile.Balanced, T0);
        Assert.Equal(P(100), d.Cpu);
        Assert.Equal(P(100 - FanController.SupportOffset), d.Gpu);
    }

    [Fact]
    public void Rising_temperature_is_applied_at_once()
    {
        _c.Decide(Snap(50, 45), FanProfile.Balanced, T0);
        var d = _c.Decide(Snap(65, 45), FanProfile.Balanced, T0.AddSeconds(1));
        Assert.Equal(P(85), d.Cpu);
    }

    [Fact]
    public void Small_drop_inside_the_hysteresis_band_keeps_the_fan()
    {
        _c.Decide(Snap(65, 45), FanProfile.Balanced, T0);
        var d = _c.Decide(Snap(63, 45), FanProfile.Balanced, T0.AddSeconds(5));
        Assert.Equal(P(85), d.Cpu);
    }

    [Fact]
    public void Large_drop_ramps_down_no_faster_than_the_slew_limit()
    {
        _c.Decide(Snap(75, 45), FanProfile.Balanced, T0);
        var d = _c.Decide(Snap(50, 45), FanProfile.Balanced, T0.AddSeconds(1));
        Assert.Equal(P(98), d.Cpu); // 100 - 2.5, rounded

        for (int i = 2; i <= 60; i++) d = _c.Decide(Snap(50, 45), FanProfile.Balanced, T0.AddSeconds(i));
        // Settles at the curve read HysteresisC hotter: Balanced(53) = 36.
        Assert.Equal(P(FanProfile.Balanced.CpuCurve.Evaluate(50 + FanController.HysteresisC)), d.Cpu);
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
        var d = _c.Decide(Snap(45, 45, boost: true), FanProfile.Quiet, T0);
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
    [InlineData(66, 80)]
    [InlineData(73, 90)]
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
    public void Immediate_threshold_is_hot_at_once()
        => Assert.True(new HotTracker().Update(Snap(50, HotTracker.ImmediateC), T0));
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
