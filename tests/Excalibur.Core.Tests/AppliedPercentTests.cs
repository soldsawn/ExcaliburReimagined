using Excalibur.Core.Fan;
using Xunit;

namespace Excalibur.Core.Tests;

public class AppliedPercentTests
{
    // Pairs seen in the service log: CPU 2715 rpm = 50%, GPU 2085 rpm = 40%.
    [Theory]
    [InlineData(2715, 54.5, 50)]
    [InlineData(2085, 52.5, 40)]
    [InlineData(0, 54.5, 0)]
    [InlineData(9000, 54.5, 100)]
    public void Percent_comes_from_the_rpm(int rpm, double rpmPerPercent, int expected)
        => Assert.Equal(expected, EcFanHardware.AppliedPercent(rpm, rpmPerPercent));
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
