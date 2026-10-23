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
