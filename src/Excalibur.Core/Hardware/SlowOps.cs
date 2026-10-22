namespace Excalibur.Core.Hardware;

/// <summary>
/// Reports EC and WMI calls that took unusually long. A collision with Windows' own EC driver
/// shows up as a stall of seconds; logging which call stalled tells our two processes' traffic
/// apart. The services point <see cref="Report"/> at their log; elsewhere it stays null.
/// </summary>
public static class SlowOps
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(500);

    public static Action<string>? Report { get; set; }

    public static void Check(string what, TimeSpan elapsed)
    {
        if (Describe(what, elapsed) is { } line) Report?.Invoke(line);
    }

    public static string? Describe(string what, TimeSpan elapsed)
        => elapsed >= Threshold ? $"slow: {what} {elapsed.TotalMilliseconds:F0} ms" : null;
}
