using Excalibur.Core.Fan;

namespace Excalibur.Core.Tests;

public class FanTargetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(100)]
    public void Percent_round_trips(int percent) => Assert.Equal(percent, FanTarget.FromPercent(percent).Percent);

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Out_of_range_percent_is_rejected(int percent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FanTarget.FromPercent(percent));

    [Fact]
    public void Raw_0xFF_is_auto() => Assert.True(FanTarget.FromRaw(0xFF)!.Value.IsAuto);

    [Theory]
    [InlineData(0x90)] // Windows' burst-mode ack, the classic stray byte
    [InlineData(0x65)]
    public void Corrupted_raw_values_are_rejected(byte raw) => Assert.Null(FanTarget.FromRaw(raw));
}

public class FanRegistersTests
{
    [Theory]
    [InlineData(0x2C, 45, true)]
    [InlineData(0x2D, 100, true)]
    [InlineData(0x2C, 0xFF, true)]
    [InlineData(0x2C, 101, false)]
    [InlineData(0xC3, 1, false)]   // Cooling Boost is never written by the service
    [InlineData(0x2E, 50, false)]  // applied percent is read-only
    public void Only_fan_targets_with_valid_values_are_writable(byte address, byte value, bool expected)
        => Assert.Equal(expected, FanRegisters.IsWritable(address, value));
}

public class FanCurveTests
{
    private static readonly FanCurve Balanced = FanProfile.Balanced.CpuCurve;

    [Theory]
    [InlineData(20, 30)]  // flat below the first point
    [InlineData(50, 30)]
    [InlineData(52, 34)]  // linear between 50->30 and 55->40
    [InlineData(65, 85)]
    [InlineData(99, 100)] // flat above the last point
    public void Evaluates_balanced(int tempC, int expected) => Assert.Equal(expected, Balanced.Evaluate(tempC));

    [Fact]
    public void Rejects_decreasing_percent()
        => Assert.Throws<ArgumentException>(() => FanCurve.Of((50, 60), (60, 40)));

    [Fact]
    public void Rejects_unsorted_temperatures()
        => Assert.Throws<ArgumentException>(() => FanCurve.Of((60, 40), (50, 60)));

    [Fact]
    public void Built_in_profiles_never_undercut_stock_from_75C()
    {
        foreach (var p in FanProfile.BuiltIn.Where(p => !p.IsEcAuto))
            Assert.True(p.CpuCurve.Evaluate(75) >= 95, $"{p.Name} gives {p.CpuCurve.Evaluate(75)}% at 75C");
    }
}
