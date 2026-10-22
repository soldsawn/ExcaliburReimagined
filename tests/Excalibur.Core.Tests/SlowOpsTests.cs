using Excalibur.Core.Hardware;
using Xunit;

namespace Excalibur.Core.Tests;

public class SlowOpsTests
{
    [Fact]
    public void Quick_calls_are_not_reported()
        => Assert.Null(SlowOps.Describe("wmi 0x0200", TimeSpan.FromMilliseconds(499)));

    [Fact]
    public void Slow_calls_are_reported_with_their_duration()
        => Assert.Equal("slow: wmi 0x0200 4012 ms", SlowOps.Describe("wmi 0x0200", TimeSpan.FromMilliseconds(4012.4)));
}
