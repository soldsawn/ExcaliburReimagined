using System.Diagnostics.Eventing.Reader;

namespace Excalibur.App;

internal enum SystemEventKind
{
    /// <summary>ACPI 13 / 15: Windows timed out talking to the embedded controller (fans, battery).</summary>
    EcError,
    /// <summary>Kernel-Power 524 (critical battery) or 41 (restarted without shutting down cleanly).</summary>
    Shutdown,
}

internal sealed record SystemEvent(DateTime AtUtc, SystemEventKind Kind, int Id);

/// <summary>
/// The Windows System log entries that matter for this app: EC timeouts, which our own EC traffic can
/// cause, and the sudden shutdowns a spoiled battery reading leads to. Reading the System log needs no
/// administrator rights. Any failure gives an empty list.
/// </summary>
internal static class SystemEventLog
{
    public static List<SystemEvent> Read(DateTime fromUtc, DateTime toUtc)
    {
        string from = fromUtc.ToString("o"), to = toUtc.ToString("o");
        string query =
            $"*[System[TimeCreated[@SystemTime>='{from}' and @SystemTime<'{to}'] and " +
            "((Provider[@Name='ACPI'] and (EventID=13 or EventID=15)) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=524 or EventID=41)))]]";
        var result = new List<SystemEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                using (record)
                    if (record.TimeCreated is { } at)
                        result.Add(new SystemEvent(at.ToUniversalTime(), record.Id is 13 or 15 ? SystemEventKind.EcError : SystemEventKind.Shutdown, record.Id));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { }
        return result;
    }
}
