using System.Management;
using Excalibur.Core.Graphics;

namespace Excalibur.FanService;

/// <summary>
/// Keeps the graphics mode reading current (WMI 0x0203): 10 s after start (out of the start's burst of firmware calls),
/// 5 s after a wake, and 3 s after the firmware's HDMI event (GMC_WMIEvent byte 1 = 4; raised when a cable goes in, not out; 3 s = the EC
/// quiet spell every firmware event starts). Runs apart from the fan loop: a slow firmware call can only delay it.
/// Started in Integrated, NVML stays closed (the GPU is off). An HDMI cable going in while Integrated brings Hybrid back,
/// as the stock software does (the port is on the NVIDIA GPU). After a wake in Integrated the read also writes the EC's GFXT
/// back to 0 (the BIOS sets it to 1 on waking).
/// </summary>
public sealed class GraphicsWorker(GraphicsModeMonitor graphics, GraphicsSwitcher switcher, PowerSignal signal, FanRuntime runtime, Excalibur.Core.Fan.FanFileLog fileLog, ILogger<GraphicsWorker> logger) : BackgroundService
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
            if (graphics.Current is { Mode: GraphicsMode.Integrated })
            {
                Excalibur.Core.Power.Nvml.Release();
                fileLog.Write(DateTimeOffset.Now, "graphics: Integrated at start, the NVIDIA driver is not asked");
            }
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
                if (why == "wake") switcher.Woke();
                if (why == "HDMI" && graphics.Current is { Mode: GraphicsMode.Integrated, HdmiConnected: true })
                {
                    fileLog.Write(DateTimeOffset.Now, "graphics: HDMI cable in while Integrated, back to Hybrid");
                    switcher.Start(GraphicsMode.Hybrid, "HDMI");
                }
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
                if (e.NewEvent["EventDetail"] is byte[] { Length: > 1 } detail && detail[1] == HdmiEvent) Schedule("HDMI", HdmiDelay);
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
