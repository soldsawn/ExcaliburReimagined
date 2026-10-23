using Excalibur.Core.Fan;

namespace Excalibur.Core.Tests;

internal static class FanTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static FanSnapshot Snap(int cpuC, int gpuC, FanTarget? cpuTarget = null, FanTarget? gpuTarget = null, bool boost = false)
        => new(cpuC, gpuC, 3600, 3300, cpuTarget ?? FanTarget.Auto, gpuTarget ?? FanTarget.Auto, 66, 63, boost);

    public static FanTarget P(int percent) => FanTarget.FromPercent(percent);

    // The built-in curves as they were until 2026-10-23, so the controller and loop tests do not follow curve tuning.
    public static readonly FanProfile Quiet = new("Quiet",
        FanCurve.Of((50, 25), (55, 30), (60, 45), (65, 70), (70, 90), (75, 100)), FanCurve.Of((50, 25), (55, 30), (60, 45), (65, 70), (70, 90), (75, 100)));
    public static readonly FanProfile Balanced = new("Balanced",
        FanCurve.Of((50, 30), (55, 40), (60, 60), (65, 85), (70, 95), (75, 100)), FanCurve.Of((50, 30), (55, 40), (60, 60), (65, 85), (70, 95), (75, 100)));
}
