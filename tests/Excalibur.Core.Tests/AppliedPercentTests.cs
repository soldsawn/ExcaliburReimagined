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
