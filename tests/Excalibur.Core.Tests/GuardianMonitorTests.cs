using Excalibur.Core.Fan;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class GuardianMonitorTests
{
    private readonly GuardianMonitor _g = new();
    private long _seq;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private HeartbeatRecord Beat(bool holds = true) => new(++_seq, T0, holds, 1234);

    private static FanSnapshot Held(int cpuC = 50, int gpuC = 45, int cpu = 30, int gpu = 30)
        => Snap(cpuC, gpuC, P(cpu), P(gpu));

    [Fact]
    public void A_live_service_holding_fans_is_left_alone()
    {
        for (int t = 0; t <= 60; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Held(), true, S(t)).Action);
    }

    [Fact]
    public void A_frozen_heartbeat_with_held_fans_is_reset_after_the_stale_window()
    {
        var last = Beat();
        _g.Evaluate(last, Held(), true, S(0));
        Assert.Equal(GuardianAction.None, _g.Evaluate(last, Held(), true, S(8)).Action);
        var v = _g.Evaluate(last, Held(), true, S(10));
        Assert.Equal(GuardianAction.ResetToAuto, v.Action);
        Assert.False(v.ServiceAlive);
    }

    [Fact]
    public void A_dead_process_is_detected_sooner()
    {
        var last = Beat();
        _g.Evaluate(last, Held(), true, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(last, Held(), false, S(4)).Action);
    }

    [Fact]
    public void Missing_heartbeat_with_held_fans_is_reset()
    {
        _g.Evaluate(null, Held(), false, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, Held(), false, S(2)).Action);
    }

    [Fact]
    public void Nothing_to_do_when_the_fans_are_already_on_the_ec()
    {
        _g.Evaluate(null, Snap(50, 45), false, S(0));
        Assert.Equal(GuardianAction.None, _g.Evaluate(null, Snap(50, 45), false, S(30)).Action);
    }

    [Fact]
    public void Unreadable_ec_and_no_service_resets_blindly()
    {
        _g.Evaluate(null, null, false, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, null, false, S(2)).Action);
    }

    [Fact]
    public void Unreadable_ec_with_a_live_service_is_left_alone()
    {
        _g.Evaluate(Beat(), null, true, S(0));
        Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), null, true, S(2)).Action);
    }

    [Fact]
    public void Sustained_heat_with_a_slow_held_fan_overrides_a_live_service()
    {
        for (int t = 0; t < 4; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Held(cpuC: 93, cpu: 40), true, S(t)).Action);
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(Beat(), Held(cpuC: 93, cpu: 40), true, S(4)).Action);
    }

    [Fact]
    public void Heat_with_fans_already_at_100_is_left_to_the_service()
    {
        for (int t = 0; t <= 20; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Held(cpuC: 95, gpuC: 90, cpu: 100, gpu: 100), true, S(t)).Action);
    }

    [Fact]
    public void A_service_that_comes_back_is_alive_again()
    {
        var last = Beat();
        _g.Evaluate(last, Held(), true, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(last, Held(), true, S(12)).Action);
        var v = _g.Evaluate(Beat(), Held(), true, S(14));
        Assert.Equal(GuardianAction.None, v.Action);
        Assert.True(v.ServiceAlive);
    }
}
