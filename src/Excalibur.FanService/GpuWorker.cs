using System.Management;
using Excalibur.Core.Gpu;
using Excalibur.Core.Hardware;

namespace Excalibur.FanService;

/// <summary>
/// Runs the graphics mode safety net as early as the service starts (<see cref="GpuModeManager.Start"/>), and goes back
/// to Hybrid when an external display is plugged in while in Integrated (firmware event 4), as the stock daemon does.
/// The event is handled after <see cref="EcQuiet.AfterEvent"/>: the EC is busy with it first.
/// </summary>
public sealed class GpuWorker(GpuModeManager gpu, FanRuntime runtime, ILogger<GpuWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _hdmi = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Run(gpu.Start, stoppingToken);
        using var watcher = runtime.Simulated ? null : StartWatcher();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _hdmi.WaitAsync(stoppingToken);
                await Task.Delay(EcQuiet.AfterEvent, stoppingToken);
                gpu.OnHdmi();
            }
        }
        catch (OperationCanceledException) { }
    }

    private ManagementEventWatcher? StartWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM GMC_WMIEvent"));
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["EventDetail"] is byte[] { Length: > 1 } d && d[1] == 4) _hdmi.Release();
            };
            watcher.Start();
            return watcher;
        }
        catch (ManagementException ex)
        {
            logger.LogWarning(ex, "external display events cannot be followed");
            return null;
        }
    }
}
