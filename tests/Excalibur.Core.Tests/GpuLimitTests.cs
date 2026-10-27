using Excalibur.Core.Power;

namespace Excalibur.Core.Tests;

public class GpuLimitTests
{
    /// <summary>A GPU whose power follows its clock as measured on the G870's RTX 4060 (2450 MHz 100 W, 1500 MHz 40 W).</summary>
    private sealed class FakeGpu : IGpuClocks
    {
        public int FreeMhz = 2450;
        public bool Idle;
        public int? Locked;
        public int Unlocks;

        public int Clock => Idle ? 300 : Math.Min(FreeMhz, Locked ?? int.MaxValue);
        public int? PowerWatts() => Idle ? 8 : (int)Math.Round(100 * Math.Pow(Clock / 2450.0, 2));
        public int? GraphicsClockMhz() => Clock;
        public int? MaxClockMhz() => 3105;
        public void Lock(int maxMhz) => Locked = maxMhz;
        public void Unlock() { Locked = null; Unlocks++; }
    }

    private readonly FakeGpu _gpu = new();
    private readonly GpuLimiter _limiter;

    public GpuLimitTests() => _limiter = new GpuLimiter(_gpu);

    private void Polls(int count, int? watts)
    {
        for (int i = 0; i < count; i++) _limiter.Apply(watts);
    }

    [Fact]
    public void A_game_at_100_W_settles_near_the_target()
    {
        Polls(10, 40);
        Assert.InRange(_gpu.PowerWatts()!.Value, 40 - GpuLimiter.Below, 40 + GpuLimiter.Above);
        Assert.Equal(_gpu.Locked, _limiter.LockMhz);
    }

    [Fact]
    public void Under_the_target_nothing_is_locked()
    {
        _gpu.FreeMhz = 1200; // a light game: ~24 W
        Polls(5, 40);
        Assert.Null(_gpu.Locked);
    }

    [Fact]
    public void An_idle_gpu_keeps_its_lock()
    {
        Polls(10, 40);
        int? held = _gpu.Locked;
        _gpu.Idle = true;
        Polls(10, 40);
        Assert.Equal(held, _gpu.Locked); // the next game starts limited, not at 100 W
    }

    [Fact]
    public void A_higher_target_raises_the_lock_and_removes_it_past_the_top_clock()
    {
        Polls(10, 40);
        Polls(30, 95);
        Assert.InRange(_gpu.PowerWatts()!.Value, 95 - GpuLimiter.Below, 95 + GpuLimiter.Above);
        _gpu.FreeMhz = 3105;
        Polls(30, 175);
        Assert.Null(_gpu.Locked);
    }

    [Fact]
    public void A_lock_the_driver_dropped_is_set_again()
    {
        Polls(10, 40);
        _gpu.Locked = null; // the GPU slept
        Polls(10, 40);
        Assert.NotNull(_gpu.Locked);
        Assert.InRange(_gpu.PowerWatts()!.Value, 40 - GpuLimiter.Below, 40 + GpuLimiter.Above);
    }

    [Fact]
    public void No_target_frees_the_gpu_and_the_first_poll_clears_a_left_over_lock()
    {
        _gpu.Locked = 900; // a service that died
        _limiter.Apply(null);
        Assert.Null(_gpu.Locked);
        Polls(10, 40);
        _limiter.Apply(null);
        Assert.Null(_gpu.Locked);
        Assert.Null(_limiter.LockMhz);
    }

    [Fact]
    public void The_lock_never_goes_below_the_floor()
    {
        Polls(20, GpuLimit.MinWatts);
        Assert.True(_gpu.Locked >= GpuLimiter.FloorMhz);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(30, true)]
    [InlineData(42, false)]
    [InlineData(175, true)]
    [InlineData(180, false)]
    public void Targets_are_30_to_175_W_in_steps_of_5(int watts, bool ok) => Assert.Equal(ok, GpuLimit.Validate(watts).Count == 0);
}
