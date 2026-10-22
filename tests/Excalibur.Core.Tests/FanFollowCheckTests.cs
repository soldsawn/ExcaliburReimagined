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
}
