using System.Management;
using Excalibur.Core.Lighting;

namespace Excalibur.FanService;

/// <summary>
/// Shows the stored keyboard light at start and again 5 s after the laptop wakes (the firmware may have reset it), and
/// follows the light key (GMC_WMIEvent byte 1 = 0): the new level is kept and the colours are sent again at it, as the
/// stock daemon does. Runs apart from the fan loop: a slow firmware call can only delay the light.
/// </summary>
public sealed class LightingWorker(LightingManager lighting, PowerSignal signal, FanRuntime runtime, ILogger<LightingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _wake = new(0);
    private volatile bool _resumed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        signal.Changed += OnPower;
        using var watcher = runtime.Simulated ? null : StartKeyWatcher();
        try
        {
            await Task.Run(lighting.Reapply, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(stoppingToken);
                if (!_resumed) continue;
                _resumed = false;
                await Task.Delay(ResumeDelay, stoppingToken);
                lighting.Reapply();
            }
        }
        catch (OperationCanceledException) { }
        finally { signal.Changed -= OnPower; }
    }

    private void OnPower(bool suspending)
    {
        if (suspending) return;
        _resumed = true;
        _wake.Release();
    }

    private ManagementEventWatcher? StartKeyWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM GMC_WMIEvent"));
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["EventDetail"] is byte[] detail && KeyboardLighting.TryParseKeyEvent(detail, out int level))
                    lighting.OnKey(level);
            };
            watcher.Start();
            return watcher;
        }
        catch (ManagementException ex)
        {
            logger.LogWarning(ex, "the light key cannot be followed");
            return null;
        }
    }
}
