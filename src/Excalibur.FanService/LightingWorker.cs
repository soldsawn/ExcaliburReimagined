using System.Management;
using Excalibur.Core.Lighting;

namespace Excalibur.FanService;

/// <summary>
/// Shows the stored keyboard light at start and again 5 s after the laptop wakes (the firmware may have reset it), and
/// follows the light key (GMC_WMIEvent byte 1 = 0): the new level is kept and the colours are sent again at it, as the
/// stock daemon does. Runs apart from the fan loop: a slow firmware call can only delay the light.
/// Key presses are gathered: the light is sent once, <see cref="KeyQuiet"/> after the last press. Five quick presses
/// sent at once (three zone commands each, while the EC was busy with the key itself) stalled the EC for 4 s and
/// Windows logged ACPI 13 (2026-10-23 18:52).
/// </summary>
public sealed class LightingWorker(LightingManager lighting, PowerSignal signal, FanRuntime runtime, ILogger<LightingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan KeyQuiet = TimeSpan.FromSeconds(1.5);
    private Timer? _keyTimer;
    private volatile int _keyLevel;
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
        finally
        {
            signal.Changed -= OnPower;
            _keyTimer?.Dispose();
        }
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
                {
                    _keyLevel = level;
                    // Each press pushes the send back; only the last level goes out.
                    if (_keyTimer is null) _keyTimer = new Timer(_ => lighting.OnKey(_keyLevel), null, KeyQuiet, Timeout.InfiniteTimeSpan);
                    else _keyTimer.Change(KeyQuiet, Timeout.InfiniteTimeSpan);
                }
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
