using Excalibur.Core.Hardware;
using Xunit;

namespace Excalibur.Core.Tests;

public class StallGuardTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Ample = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_call_that_answers_returns_its_result()
        => Assert.Equal(7, new StallGuard(Ample).Run("wmi 0x0200", () => 7));

    [Fact]
    public void A_failing_call_throws_its_own_exception()
        => Assert.Throws<FormatException>(() => new StallGuard(Ample).Run<int>("wmi 0x0200", () => throw new FormatException()));

    [Fact]
    public void A_hung_call_stops_the_wait_and_holds_back_the_next_until_it_returns()
    {
        var lines = new List<string>();
        var guard = new StallGuard(Short, m => { lock (lines) lines.Add(m); });
        using var release = new ManualResetEventSlim();
        int calls = 0;

        Assert.Throws<WmiStalledException>(() => guard.Run("wmi 0x0200", () => { calls++; release.Wait(); return 1; }));
        Assert.True(guard.IsStalled);
        Assert.Throws<WmiStalledException>(() => guard.Run("wmi 0x0200", () => { calls++; return 2; }));
        Assert.Equal(1, calls); // nothing piled on the hung call

        release.Set();
        SpinWait.SpinUntil(() => !guard.IsStalled, TimeSpan.FromSeconds(5));
        Assert.Equal(3, guard.Run("wmi 0x0200", () => 3));
        lock (lines)
        {
            Assert.StartsWith("!! wmi 0x0200 has not answered", lines[0]);
            Assert.Contains("firmware calls resume", lines[1]);
        }
    }
}
