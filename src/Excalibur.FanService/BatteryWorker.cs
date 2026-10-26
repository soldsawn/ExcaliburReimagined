using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;
using Excalibur.Core.Power;
using Excalibur.Core.Settings;

namespace Excalibur.FanService;

/// <summary>
/// The battery, on its own task: a slow WMI answer can only delay the battery page. Reads the health once and then once a
/// day, and drives <see cref="BatteryRecorder"/> (sparse reads on battery, kept intervals to the csv log). No battery read
/// while the EC is held quiet; a failed health read is tried again after <see cref="Retry"/>.
/// </summary>
public sealed class BatteryWorker(
    BatteryMonitor battery,
    BatteryRecorder recorder,
    BatteryCsvLog csv,
    BatteryEstimates estimates,
    FanControlLoop loop,
    PowerManager power,
    ServiceSettingsStore settings,
    FanRuntime runtime,
    FanFileLog fileLog,
    ILogger<BatteryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
    // Not with the service's start: a restart after Windows' first minutes sent it with the fan, power and lighting
    // calls, and it stalled 4.8 s among them before an ACPI 13 (2026-10-26 22:46).
    private static readonly TimeSpan HealthAfterStart = TimeSpan.FromSeconds(30);
    private DateTimeOffset _healthAt = DateTimeOffset.Now + HealthAfterStart;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        recorder.Recorded += OnRecorded;
        try
        {
            using var timer = new PeriodicTimer(Period);
            do
            {
                try { await Task.Run(TickOnce, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The battery page is a convenience: nothing here may stop the service.
                    logger.LogWarning(ex, "battery tick failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally { recorder.Recorded -= OnRecorded; }
    }

    private void TickOnce()
    {
        var now = DateTimeOffset.Now;
        bool ecBusy = !runtime.Simulated && EcQuiet.IsQuiet;
        if (!ecBusy && now >= _healthAt)
            _healthAt = now + (battery.ReadHealth() ? Every : Retry);
        recorder.Interval = TimeSpan.FromMinutes(settings.Current.BatteryReadMinutes);
        var status = power.Status;
        recorder.Tick(now, battery.Live(), status.Mode, loop.Status.Profile, ecBusy, status.Cpu?.Watts);
    }

    private void OnRecorded(BatteryInterval i)
    {
        csv.Append(i);
        estimates.Add(i);
        fileLog.Write(DateTimeOffset.Now, $"battery: {i.Average.Watts:F1} W over {i.Average.Span.TotalMinutes:F0} min ({i.Mode}, {i.Profile}, brightness {i.Brightness?.ToString() ?? "-"}, CPU {i.CpuWatts?.ToString("F1") ?? "-"} W)");
    }
}
