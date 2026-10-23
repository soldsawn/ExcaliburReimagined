using Excalibur.Core.Fan;
using static Excalibur.Core.Tests.FanTestData;

namespace Excalibur.Core.Tests;

public class GuardianMonitorTests
{
    private readonly GuardianMonitor _g = new();
    private long _seq;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private HeartbeatRecord Beat(bool holds = true) => new(++_seq, T0, holds, 1234);

    private static FanSensors Sensors(int cpuC = 50, int gpuC = 45, int cpu = 30, int gpu = 30) => new(cpuC, gpuC, cpu, gpu);

    [Fact]
    public void A_live_service_holding_fans_is_left_alone()
    {
        for (int t = 0; t <= 60; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Sensors(), true, S(t)).Action);
    }

    [Fact]
    public void A_frozen_heartbeat_is_reset_after_the_stale_window()
    {
        var last = Beat();
        _g.Evaluate(last, Sensors(), true, S(0));
        Assert.Equal(GuardianAction.None, _g.Evaluate(last, Sensors(), true, S(18)).Action);
        var v = _g.Evaluate(last, Sensors(), true, S(20));
        Assert.Equal(GuardianAction.ResetToAuto, v.Action);
        Assert.False(v.ServiceAlive);
    }

    [Fact]
    public void A_14_second_stall_of_a_live_service_is_not_a_death()
    {
        var last = Beat();
        _g.Evaluate(last, Sensors(), true, S(0));
        Assert.True(_g.Evaluate(last, Sensors(), true, S(14)).ServiceAlive);
        Assert.True(_g.Evaluate(Beat(), Sensors(), true, S(16)).ServiceAlive);
    }

    [Fact]
    public void A_dead_process_is_detected_sooner()
    {
        var last = Beat();
        _g.Evaluate(last, Sensors(), true, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(last, Sensors(), false, S(4)).Action);
    }

    [Fact]
    public void Missing_heartbeat_is_reset_even_when_the_sensors_are_unreadable()
    {
        _g.Evaluate(null, null, false, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, null, false, S(2)).Action);
    }

    [Fact]
    public void A_dead_service_is_handed_back_once_not_every_evaluation()
    {
        _g.Evaluate(null, Sensors(), false, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, Sensors(), false, S(2)).Action);
        _g.HandedBack(S(2));
        for (int t = 4; t <= 60; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(null, Sensors(), false, S(t)).Action);
    }

    [Fact]
    public void A_failed_hand_back_is_retried()
    {
        _g.Evaluate(null, Sensors(), false, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, Sensors(), false, S(2)).Action);
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(null, Sensors(), false, S(4)).Action);
    }

    [Fact]
    public void A_service_that_dies_again_is_handed_back_again()
    {
        var last = Beat();
        _g.Evaluate(last, Sensors(), true, S(0));
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(last, Sensors(), true, S(22)).Action);
        _g.HandedBack(S(22));
        last = Beat();
        Assert.True(_g.Evaluate(last, Sensors(), true, S(24)).ServiceAlive);
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(last, Sensors(), true, S(46)).Action);
    }

    [Fact]
    public void Unreadable_sensors_with_a_live_service_are_left_alone()
    {
        _g.Evaluate(Beat(), null, true, S(0));
        Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), null, true, S(2)).Action);
    }

    [Fact]
    public void Sustained_heat_with_a_slow_fan_overrides_a_live_service()
    {
        for (int t = 0; t < 8; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Sensors(cpuC: 93, cpu: 40), true, S(t)).Action);
        var v = _g.Evaluate(Beat(), Sensors(cpuC: 93, cpu: 40), true, S(8));
        Assert.Equal(GuardianAction.ResetToAuto, v.Action);
        Assert.True(v.ServiceAlive);
    }

    [Fact]
    public void After_overriding_a_live_service_it_waits_before_doing_it_again()
    {
        for (int t = 0; t <= 8; t += 2) _g.Evaluate(Beat(), Sensors(cpuC: 93, cpu: 40), true, S(t));
        _g.HandedBack(S(8));
        for (int t = 10; t < 68; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Sensors(cpuC: 93, cpu: 40), true, S(t)).Action);
        Assert.Equal(GuardianAction.ResetToAuto, _g.Evaluate(Beat(), Sensors(cpuC: 93, cpu: 40), true, S(68)).Action);
    }

    [Fact]
    public void Heat_with_fans_already_at_full_speed_is_left_to_the_service()
    {
        for (int t = 0; t <= 20; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(), Sensors(cpuC: 95, gpuC: 90, cpu: 100, gpu: 100), true, S(t)).Action);
    }

    [Fact]
    public void Heat_while_the_ec_runs_the_fans_is_not_the_services_fault()
    {
        for (int t = 0; t <= 20; t += 2)
            Assert.Equal(GuardianAction.None, _g.Evaluate(Beat(holds: false), Sensors(cpuC: 93, cpu: 40), true, S(t)).Action);
    }
}
