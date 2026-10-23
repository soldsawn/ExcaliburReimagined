using System.Diagnostics.Eventing.Reader;
using System.Text;

namespace Excalibur.Core.Hardware;

/// <summary>
/// The last <see cref="Keep"/> of this process's EC and firmware calls, in memory only (every call already passes
/// <see cref="SlowOps.Check"/>). When Windows logs an EC timeout (ACPI 13) or unasked data (ACPI 15), or a call of ours
/// is slow, the minute before goes to the log: which call, from which process, how long. Until then nothing is
/// written, so it costs a list append per call. Investigation aid for the EC / battery collisions (docs/12).
/// </summary>
public static class EcTrace
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Dumped = TimeSpan.FromSeconds(60);
    /// <summary>A slow call dumps at most this often (a stuck EC makes many in a row).</summary>
    public static readonly TimeSpan SlowDumpEvery = TimeSpan.FromSeconds(60);

    private readonly record struct Entry(DateTime At, string What, double Ms);

    private static readonly Queue<Entry> Entries = new();
    private static readonly object Gate = new();
    private static DateTime _lastSlowDump = DateTime.MinValue;
    private static EventLogWatcher? _acpi;

    /// <summary>Where dumps go; null (the default) keeps the trace off entirely.</summary>
    public static Action<string>? Report { get; set; }

    public static void Record(string what, TimeSpan elapsed)
    {
        if (Report is null) return;
        var now = DateTime.Now;
        bool dumpSlow = false;
        lock (Gate)
        {
            Entries.Enqueue(new Entry(now, what, elapsed.TotalMilliseconds));
            while (Entries.Count > 0 && now - Entries.Peek().At > Keep) Entries.Dequeue();
            if (elapsed >= SlowOps.Threshold && now - _lastSlowDump >= SlowDumpEvery)
            {
                _lastSlowDump = now;
                dumpSlow = true;
            }
        }
        if (dumpSlow) Dump($"slow {what}", now);
    }

    /// <summary>Writes the calls of the minute before <paramref name="at"/>, one line per call.</summary>
    public static void Dump(string why, DateTime at)
    {
        if (Report is not { } report) return;
        Entry[] entries;
        lock (Gate) entries = Entries.Where(e => e.At >= at - Dumped && e.At <= at + TimeSpan.FromSeconds(5)).ToArray();
        var sb = new StringBuilder($"ec trace ({why}, {at:HH:mm:ss.fff}): {entries.Length} calls in the {Dumped.TotalSeconds:F0} s before");
        foreach (var e in entries) sb.Append($"\n    {e.At:HH:mm:ss.fff}  {e.What,-18} {e.Ms,6:F0} ms");
        report(sb.ToString());
    }

    /// <summary>Follows the System log for ACPI 13 / 15 (an event, no polling) and dumps the trace on each.</summary>
    public static void WatchAcpi(Action<string>? log = null)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='ACPI'] and (EventID=13 or EventID=15)]]");
            var watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += (_, e) =>
            {
                using var record = e.EventRecord;
                if (record is null) return;
                var at = record.TimeCreated ?? DateTime.Now;
                log?.Invoke($"windows: ACPI {record.Id} at {at:HH:mm:ss.fff}");
                Dump($"ACPI {record.Id}", at);
            };
            watcher.Enabled = true;
            _acpi = watcher;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            log?.Invoke($"ec trace: the System log cannot be followed ({ex.Message})");
        }
    }
}
