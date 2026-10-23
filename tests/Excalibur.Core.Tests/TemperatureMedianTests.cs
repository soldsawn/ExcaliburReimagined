using Excalibur.Core.Fan;
using Xunit;

namespace Excalibur.Core.Tests;

public class TemperatureMedianTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_single_spike_does_not_move_the_median()
    {
        // 2026-10-23 07:24:06, MSR at light load: one 100 among ~51s.
        var median = new TemperatureMedian(TimeSpan.FromSeconds(2));
        int[] readings = [51, 50, 52, 51, 100, 51, 50, 51, 52, 51];
        for (int i = 0; i < readings.Length; i++) median.Add(readings[i], Ms(200 * i));
        Assert.Equal(51, median.Value(Ms(1800)));
    }

    [Fact]
    public void A_real_climb_shows_once_it_is_half_the_window()
    {
        var median = new TemperatureMedian(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 5; i++) median.Add(51, Ms(200 * i));
        for (int i = 5; i < 11; i++) median.Add(96, Ms(200 * i));
        Assert.Equal(96, median.Value(Ms(2000)));
    }

    [Fact]
    public void Old_readings_leave_the_window()
    {
        var median = new TemperatureMedian(TimeSpan.FromSeconds(2));
        median.Add(50, Ms(0));
        median.Add(96, Ms(2500));
        Assert.Equal(96, median.Value(Ms(2500)));
    }

    [Fact]
    public void No_recent_reading_is_null()
    {
        var median = new TemperatureMedian(TimeSpan.FromSeconds(2));
        Assert.Null(median.Value(Ms(0)));
        median.Add(60, Ms(0));
        Assert.Null(median.Value(Ms(2100)));
    }
}
