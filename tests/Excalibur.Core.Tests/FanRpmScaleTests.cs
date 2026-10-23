using Excalibur.Core.Fan;
using Xunit;

namespace Excalibur.Core.Tests;

public class FanRpmScaleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpm-scale").FullName;
    private string PathOf => Path.Combine(_dir, FanRpmScale.FileName);

    public void Dispose() => Directory.Delete(_dir, true);

    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);

    // The tester's G770: 63% held for minutes ran at 3654 RPM (58.0 per percent).
    private static void Hold(FanRpmScale scale, FanId fan, int percent, int rpm, int from, int seconds, bool boost = false)
    {
        for (int i = from; i < from + seconds; i++) scale.Observe(fan, FanTarget.FromPercent(percent), rpm, boost, S(i));
    }

    [Fact]
    public void A_steady_target_moves_the_scale_toward_rpm_per_percent()
    {
        var scale = new FanRpmScale();
        Hold(scale, FanId.Cpu, 63, 3654, 0, 120);
        Assert.Equal(58.0, scale.Of(FanId.Cpu), 1);
        Assert.Equal(63, scale.PercentOf(FanId.Cpu, 3654));
        Assert.Equal(FanRpmScale.DefaultGpu, scale.Of(FanId.Gpu));
    }

    [Fact]
    public void Nothing_is_learnt_before_the_target_stood_long_enough()
    {
        var scale = new FanRpmScale();
        Hold(scale, FanId.Cpu, 63, 3654, 0, 8);
        Assert.Equal(FanRpmScale.DefaultCpu, scale.Of(FanId.Cpu));
    }

    [Theory]
    [InlineData(25, 1450, false)] // below the learnt range
    [InlineData(100, 5200, false)] // the G870 saturates near 100%
    [InlineData(63, 3654, true)] // Cooling Boost runs the fans flat out whatever the target
    [InlineData(50, 1000, false)] // 20 RPM per percent: not a fan reading
    public void Readings_that_say_nothing_about_the_scale_are_left_out(int percent, int rpm, bool boost)
    {
        var scale = new FanRpmScale();
        Hold(scale, FanId.Cpu, percent, rpm, 0, 60, boost);
        Assert.Equal(FanRpmScale.DefaultCpu, scale.Of(FanId.Cpu));
    }

    [Fact]
    public void Ec_automatic_is_left_out()
    {
        var scale = new FanRpmScale();
        for (int i = 0; i < 60; i++) scale.Observe(FanId.Cpu, FanTarget.Auto, 4000, false, S(i));
        Assert.Equal(FanRpmScale.DefaultCpu, scale.Of(FanId.Cpu));
    }

    [Fact]
    public void A_known_version_starts_from_its_measured_scale_and_saves_it_for_the_guardian()
    {
        var service = new FanRpmScale(PathOf);
        service.UseEcVersion("P003K0");
        Assert.Equal(58.0, service.Of(FanId.Cpu));
        Assert.Equal(59.0, service.Of(FanId.Gpu));

        var guardian = new FanRpmScale(PathOf);
        Assert.Equal(58.0, guardian.Of(FanId.Cpu));
        Assert.Equal(59.0, guardian.Of(FanId.Gpu));
    }

    [Fact]
    public void A_learnt_scale_is_kept_across_starts_of_the_same_version_only()
    {
        var first = new FanRpmScale(PathOf);
        first.UseEcVersion("P009C0");
        Hold(first, FanId.Gpu, 53, 3129, 0, 200);

        var again = new FanRpmScale(PathOf);
        again.UseEcVersion("P009C0");
        Assert.Equal(59.0, again.Of(FanId.Gpu), 0);

        var updated = new FanRpmScale(PathOf);
        updated.UseEcVersion("P008B0"); // a BIOS update brought another EC
        Assert.Equal(52.5, updated.Of(FanId.Gpu));
    }

    [Fact]
    public void The_guardian_rereads_the_file_at_most_every_minute()
    {
        var service = new FanRpmScale(PathOf);
        service.UseEcVersion("P008B0");
        var guardian = new FanRpmScale(PathOf);
        guardian.Refresh(S(0));

        new FanRpmScale(PathOf).UseEcVersion("P003K0");
        guardian.Refresh(S(30));
        Assert.Equal(54.5, guardian.Of(FanId.Cpu));
        guardian.Refresh(S(61));
        Assert.Equal(58.0, guardian.Of(FanId.Cpu));
    }

    [Fact]
    public void A_damaged_file_is_ignored()
    {
        File.WriteAllText(PathOf, "P003K0 abc 59");
        Assert.Equal(FanRpmScale.DefaultCpu, new FanRpmScale(PathOf).Of(FanId.Cpu));
    }
}
