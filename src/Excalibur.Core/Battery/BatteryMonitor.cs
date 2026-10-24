using System.Diagnostics;

namespace Excalibur.Core.Battery;

/// <summary>
/// Keeps the battery's health as last read, so the pipe answers from memory and never waits on WMI.
/// The service decides when to read (<see cref="ReadHealth"/>); the live part costs nothing and is read on each status.
/// </summary>
public sealed class BatteryMonitor(IBatteryHardware hardware, Action<string>? log = null)
{
    public BatteryHealth? Health { get; private set; }

    /// <summary>Reads the health now; false when it failed (kept as before, try again later).</summary>
    public bool ReadHealth()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var health = hardware.ReadHealth();
            if (health is not null) Health = health;
            log?.Invoke(health is null
                ? $"battery: none found ({watch.ElapsedMilliseconds} ms)"
                : $"battery: {health.FullMWh}/{health.DesignedMWh} mWh ({health.Percent}%), {health.Chemistry ?? "?"}, " +
                  $"cycles {health.Cycles?.ToString() ?? "-"} ({watch.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            log?.Invoke($"battery: read failed after {watch.ElapsedMilliseconds} ms: {ex.Message}");
            return false;
        }
    }

    public BatteryLive? Live() => hardware.ReadLive();
}
