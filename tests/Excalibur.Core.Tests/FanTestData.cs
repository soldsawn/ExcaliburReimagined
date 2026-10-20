using Excalibur.Core.Fan;

namespace Excalibur.Core.Tests;

internal static class FanTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static FanSnapshot Snap(int cpuC, int gpuC, FanTarget? cpuTarget = null, FanTarget? gpuTarget = null, bool boost = false)
        => new(cpuC, gpuC, 3600, 3300, cpuTarget ?? FanTarget.Auto, gpuTarget ?? FanTarget.Auto, 66, 63, boost);

    public static FanTarget P(int percent) => FanTarget.FromPercent(percent);
}
