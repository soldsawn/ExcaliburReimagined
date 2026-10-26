using Excalibur.Core.Fan;
using Xunit;

namespace Excalibur.Core.Tests;

public class AppliedPercentTests
{
    // Pairs seen in the service log: CPU 2715 rpm = 50%, GPU 2085 rpm = 40% (G870).
    [Theory]
    [InlineData(FanId.Cpu, 2715, 50)]
    [InlineData(FanId.Gpu, 2085, 40)]
    [InlineData(FanId.Cpu, 0, 0)]
    [InlineData(FanId.Cpu, 9000, 100)]
    public void Percent_comes_from_the_rpm(FanId fan, int rpm, int expected)
        => Assert.Equal(expected, new FanRpmScale().PercentOf(fan, rpm));
}

public class BoostEventTests
{
    // Captured live on the G870 (Fn+1 pressed twice): 00 03 00 01 01 ... then 00 03 00 00 01 ...
    [Theory]
    [InlineData(new byte[] { 0, 3, 0, 1, 1, 0, 0, 0 }, true, true)]
    [InlineData(new byte[] { 0, 3, 0, 0, 1, 0, 0, 0 }, true, false)]
    [InlineData(new byte[] { 0, 7, 0, 1, 1, 0, 0, 0 }, false, false)]
    [InlineData(new byte[] { 0, 3 }, false, false)]
    public void Fn1_event_is_recognised(byte[] detail, bool recognised, bool on)
    {
        Assert.Equal(recognised, EcFanHardware.TryParseBoostEvent(detail, out bool actual));
        Assert.Equal(on, actual);
    }
}

public class GpuTemperatureTests
{
    [Fact]
    public void A_real_ec_reading_is_used_and_the_driver_is_not_asked()
    {
        bool asked = false;
        var r = EcFanHardware.ResolveGpuTemperature(52, 70, () => { asked = true; return 60; });
        Assert.Equal((52, GpuTempSource.Ec), r);
        Assert.False(asked);
    }

    // The G870's EC with the GPU switched off (2026-10-23): 30 throughout, once 0.
    [Theory]
    [InlineData(30)]
    [InlineData(0)]
    public void The_ec_placeholder_falls_back_to_the_driver(int ecC)
        => Assert.Equal((47, GpuTempSource.Driver), EcFanHardware.ResolveGpuTemperature(ecC, 70, () => 47));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(200)]
    public void With_no_driver_reading_the_gpu_fan_follows_the_cpu(int? driverC)
        => Assert.Equal((70, GpuTempSource.Cpu), EcFanHardware.ResolveGpuTemperature(30, 70, () => driverC));
}
