using Excalibur.Core.Fan;

namespace Excalibur.Core.Tests;

public class FanFollowCheckTests
{
    private readonly FanFollowCheck _c = new();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private FanFollow Check(int percent, double at, bool boost = false) => _c.Check(FanId.Cpu, percent, boost, S(at), out _);

    [Fact]
    public void Nothing_is_checked_before_the_fan_had_time()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(80), 30, S(0));
        Assert.Equal(FanFollow.None, Check(30, 4));
        Assert.Equal(FanFollow.Rewrite, Check(30, 5));
    }

    [Fact]
    public void A_fan_close_to_its_target_is_fine()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(60), 30, S(0));
        Assert.Equal(FanFollow.None, Check(56, 5));
        Assert.Equal(FanFollow.None, Check(30, 20)); // done: nothing pending any more
    }

    [Theory]
    [InlineData(30, 80, 55)] // speeding up, exactly half way
    [InlineData(80, 30, 55)] // slowing down, exactly half way
    public void A_fan_at_least_half_way_there_is_fine(int start, int target, int now)
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(target), start, S(0));
        Assert.Equal(FanFollow.None, Check(now, 5));
    }

    [Theory]
    [InlineData(93, 93)] // already near the top when written (the hot start, 2026-10-30 13:33)
    [InlineData(80, 90)]
    public void A_fan_at_its_top_speed_follows_a_full_target(int start, int now)
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(100), start, S(0));
        Assert.Equal(FanFollow.None, Check(now, 5));
    }

    [Fact]
    public void A_fan_well_below_its_top_speed_does_not_follow_a_full_target()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(100), 85, S(0));
        Assert.Equal(FanFollow.Rewrite, Check(86, 5));
    }

    [Fact]
    public void A_fan_the_EC_is_still_speeding_up_gets_more_time()
    {
        // The EC ramps the PWM a step at a time: 50 -> 100 is not half way after 5 s but keeps closing in.
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(100), 50, S(0));
        Assert.Equal(FanFollow.None, Check(60, 5));
        Assert.Equal(FanFollow.None, Check(70, 10));
        Assert.Equal(FanFollow.Rewrite, Check(71, 15)); // stalled
        Assert.Equal(FanFollow.None, Check(80, 20));    // moving again after the rewrite
        Assert.Equal(FanFollow.None, Check(91, 25));    // at the top: done
        Assert.Equal(FanFollow.None, Check(50, 40));
    }

    [Fact]
    public void Closing_in_waits_at_most_thirty_seconds()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(100), 20, S(0));
        for (int i = 1; i <= 5; i++) Assert.Equal(FanFollow.None, Check(20 + 4 * i, 5 * i));
        Assert.Equal(FanFollow.Rewrite, Check(48, 30));
    }

    [Fact]
    public void A_fan_that_does_not_follow_is_written_again_then_confirmed_once()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(80), 30, S(0));
        Assert.Equal(FanFollow.Rewrite, _c.Check(FanId.Cpu, 32, false, S(5), out var target));
        Assert.Equal(80, target.Percent);
        Assert.Equal(FanFollow.None, Check(32, 9));
        Assert.Equal(FanFollow.Confirm, Check(32, 10));
        Assert.Equal(FanFollow.None, Check(32, 30));
    }

    [Fact]
    public void A_rewrite_that_works_ends_the_check()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(80), 30, S(0));
        Assert.Equal(FanFollow.Rewrite, Check(30, 5));
        Assert.Equal(FanFollow.None, Check(78, 10));
        Assert.Equal(FanFollow.None, Check(30, 20));
    }

    [Fact]
    public void Cooling_boost_and_the_safe_state_are_not_checked()
    {
        _c.Wrote(FanId.Cpu, FanTarget.FromPercent(40), 30, S(0));
        Assert.Equal(FanFollow.None, Check(100, 5, boost: true));
        Assert.Equal(FanFollow.None, Check(100, 10));
        _c.Wrote(FanId.Cpu, FanTarget.Auto, 30, S(20));
        Assert.Equal(FanFollow.None, Check(60, 30));
    }

    [Fact]
    public void Each_fan_is_checked_on_its_own()
    {
        _c.Wrote(FanId.Gpu, FanTarget.FromPercent(80), 30, S(0));
        Assert.Equal(FanFollow.None, Check(30, 5));
        Assert.Equal(FanFollow.Rewrite, _c.Check(FanId.Gpu, 30, false, S(5), out _));
    }

    [Theory]
    [InlineData(55, 50, false, 4.0)]  // calm: the WMI block every 4 s
    [InlineData(72, 50, false, 1.5)]  // warm CPU
    [InlineData(55, 70, false, 1.5)]  // warm GPU
    [InlineData(55, 50, true, 1.5)]   // a target waits for its check
    [InlineData(null, 50, false, 1.5)] // no CPU sensor
    public void The_WMI_sensor_block_is_read_less_often_while_calm(int? cpuC, int gpuC, bool pending, double seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), EcFanHardware.WmiInterval(cpuC, gpuC, pending));

    [Fact]
    public void A_pending_target_is_reported_until_checked()
    {
        Assert.False(_c.AnyPending);
        _c.Wrote(FanId.Gpu, FanTarget.FromPercent(60), 30, S(0));
        Assert.True(_c.AnyPending);
        Check(30, 1);
        _c.Check(FanId.Gpu, 58, false, S(5), out _);
        Assert.False(_c.AnyPending);
    }
}
