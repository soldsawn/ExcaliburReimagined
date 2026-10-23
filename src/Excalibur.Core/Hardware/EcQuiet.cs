namespace Excalibur.Core.Hardware;

/// <summary>
/// A few seconds in which this process leaves the EC alone because the EC is busy with its own work: a firmware event
/// (light key, Fn keys, the charger) or a change of power source. Both ACPI 13 timeouts of 2026-10-23 came seconds
/// after one (18:52 quick light-key presses, 19:20 charger plugged in + power mode sent) while our once-a-second sensor
/// read went on. Readers keep their last values meanwhile; the CPU temperature (MSR) stays live. Per process: the
/// service and the guardian each see the firmware events themselves.
/// </summary>
public static class EcQuiet
{
    public static readonly TimeSpan AfterEvent = TimeSpan.FromSeconds(3);
    /// <summary>8 s felt long for the profile to follow the charger (user, 2026-10-23); the 19:20 stall began 1-4 s after
    /// the plug. The charger raises no firmware event here: the 2-s power poll sees it, so the profile follows in 4-6 s.</summary>
    public static readonly TimeSpan AfterCharger = TimeSpan.FromSeconds(4);

    private static long _untilMs;

    /// <summary>Where each hold is noted (the service's log); null logs nothing.</summary>
    public static Action<string>? Report { get; set; }

    public static bool IsQuiet => Environment.TickCount64 < Interlocked.Read(ref _untilMs);

    /// <summary>Keeps the EC quiet for at least <paramref name="span"/> from now (a longer hold already running stays).</summary>
    public static void Hold(TimeSpan span, string why)
    {
        long now = Environment.TickCount64, until = now + (long)span.TotalMilliseconds, seen;
        do
        {
            seen = Interlocked.Read(ref _untilMs);
            if (seen >= until) return;
        }
        while (Interlocked.CompareExchange(ref _untilMs, until, seen) != seen);
        // A held light key sends ~6 events a second: only the start of a quiet spell is noted, not each extension.
        if (seen > now) return;
        EcTrace.Record($"quiet {span.TotalSeconds:F0}s ({why})", TimeSpan.Zero);
        Report?.Invoke($"ec quiet {span.TotalSeconds:F0} s ({why})");
    }
}
