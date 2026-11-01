using System.Management;
using Excalibur.Core.Graphics;

namespace Excalibur.FanService;

/// <summary>
/// Keeps the graphics mode reading current (WMI 0x0203): 10 s after start (out of the start's burst of firmware calls),
/// 5 s after a wake, and 3 s after the firmware's HDMI event (GMC_WMIEvent byte 1 = 4, a cable in or out; 3 s = the EC
/// quiet spell every firmware event starts). Runs apart from the fan loop: a slow firmware call can only delay it.
/// </summary>
public sealed class GraphicsWorker(GraphicsModeMonitor graphics, PowerSignal signal, FanRuntime runtime, Excalibur.Core.Fan.FanFileLog fileLog, ILogger<GraphicsWorker> logger) : BackgroundService
{
    public const byte HdmiEvent = 4;
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10), ResumeDelay = TimeSpan.FromSeconds(5), HdmiDelay = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _wake = new(0);
    private volatile string? _why;
    private long _dueTicks; // UTC ticks; set from the event thread

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        signal.Changed += OnPower;
        using var watcher = runtime.Simulated ? null : StartEventWatcher();
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            await Task.Run(() => graphics.Refresh("start"), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(stoppingToken);
                // Several events in a row: wait for the last one's delay, then read once.
                TimeSpan wait;
                while ((wait = new DateTime(Interlocked.Read(ref _dueTicks), DateTimeKind.Utc) - DateTime.UtcNow) > TimeSpan.Zero)
                    await Task.Delay(wait, stoppingToken);
                if (_why is not { } why) continue;
                _why = null;
                await Task.Run(() => graphics.Refresh(why), stoppingToken);
                // Diagnosis (2026-11-02: a cable pulled out still read "in"): what the BIOS says after its HDMI event.
                if (why == "HDMI" && graphics.Current is { } now)
                    fileLog.Write(DateTimeOffset.Now, $"graphics: after the HDMI event the BIOS reports the cable {(now.HdmiConnected ? "in" : "out")}");
            }
        }
        catch (OperationCanceledException) { }
        finally { signal.Changed -= OnPower; }
    }

    private void OnPower(bool suspending)
    {
        if (!suspending) Schedule("wake", ResumeDelay);
    }

    private void Schedule(string why, TimeSpan delay)
    {
        _why = why;
        Interlocked.Exchange(ref _dueTicks, (DateTime.UtcNow + delay).Ticks);
        _wake.Release();
    }

    private ManagementEventWatcher? StartEventWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM GMC_WMIEvent"));
            watcher.EventArrived += (_, e) =>
            {
                if (e.NewEvent["EventDetail"] is not byte[] { Length: > 4 } detail || detail[1] != HdmiEvent) return;
                // Byte 4 = the HDMI line when the event was raised (DSDT _L12 sets GWMI.HDMI from the same GPIO).
                fileLog.Write(DateTimeOffset.Now, $"graphics: HDMI event, the cable is {(detail[4] == 1 ? "in" : "out")} by the event");
                Schedule("HDMI", HdmiDelay);
            };
            watcher.Start();
            return watcher;
        }
        catch (ManagementException ex)
        {
            logger.LogWarning(ex, "the HDMI cable cannot be followed");
            return null;
        }
    }
}
