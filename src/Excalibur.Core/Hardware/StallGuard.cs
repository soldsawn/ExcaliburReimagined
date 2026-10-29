using System.Diagnostics;

namespace Excalibur.Core.Hardware;

/// <summary>A firmware call did not answer in time, or an earlier one still has not (then nothing was sent).</summary>
// An InvalidOperationException: the callers that treat a missing WMI class as a failed call treat this the same.
public sealed class WmiStalledException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs each firmware call on a pool thread and waits at most the timeout for it. Windows' WMI service can hang for tens
/// of minutes under heavy load (2026-10-29 00:39-02:26, a local AI model filling the GPU's memory: three calls of 28-42 min,
/// ended by WMI's own "Throttling Idle Tasks", while the EC lock the call held kept the guardian from handing the fans
/// back). A hung call cannot be cancelled, but the caller stops waiting and releases what it holds. Until the hung call
/// returns no other is sent: the firmware keeps a single response buffer.
/// </summary>
public sealed class StallGuard(TimeSpan timeout, Action<string>? log = null)
{
    /// <summary>Normal calls that stall on an EC collision come back within 3-4.5 s (the ACPI 13 cases).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private volatile Task? _stalled;

    public bool IsStalled => _stalled is not null;

    public void ThrowIfStalled(string what)
    {
        if (_stalled is not null) throw new WmiStalledException($"{what} not sent: an earlier firmware call has not returned.");
    }

    public T Run<T>(string what, Func<T> call)
    {
        ThrowIfStalled(what);
        var task = Task.Run(call);
        bool done;
        try { done = task.Wait(timeout); }
        catch (AggregateException) { done = true; }
        if (done) return task.GetAwaiter().GetResult();

        var since = Stopwatch.StartNew();
        _stalled = task;
        log?.Invoke($"!! {what} has not answered for {timeout.TotalSeconds:F0} s (Windows' WMI is stalling); no firmware calls until it does");
        task.ContinueWith(t =>
        {
            _ = t.Exception; // observed: a call that failed late has nobody left to tell
            _stalled = null;
            log?.Invoke($"{what} answered after {(timeout + since.Elapsed).TotalSeconds:F0} s; firmware calls resume");
        }, TaskScheduler.Default);
        throw new WmiStalledException($"{what} did not answer within {timeout.TotalSeconds:F0} s.");
    }
}
