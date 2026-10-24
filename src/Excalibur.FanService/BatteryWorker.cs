using Excalibur.Core.Battery;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;

namespace Excalibur.FanService;

/// <summary>
/// Reads the battery's health once and then once a day, on its own task: a slow WMI answer can only delay the battery page.
/// Not in Windows' first <see cref="EcFanHardware.NoRefreshAfterBoot"/> (the driver may ask the EC, and the EC is busy then,
/// 2026-10-24) and not while the EC is held quiet; a failed read is tried again after <see cref="Retry"/>.
/// </summary>
public sealed class BatteryWorker(BatteryMonitor battery, FanRuntime runtime, ILogger<BatteryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (!runtime.Simulated && (TimeSpan.FromMilliseconds(Environment.TickCount64) < EcFanHardware.NoRefreshAfterBoot || EcQuiet.IsQuiet))
                    await Task.Delay(Wait, stoppingToken);
                bool ok;
                try { ok = await Task.Run(battery.ReadHealth, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The battery page is a convenience: nothing here may stop the service.
                    logger.LogWarning(ex, "battery read failed");
                    ok = false;
                }
                await Task.Delay(ok ? Every : Retry, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
