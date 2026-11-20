using Excalibur.Core.Power;

namespace Excalibur.FanService;

/// <summary>
/// Runs the power logic beside the fan loop, on its own timer: a slow or stuck firmware call
/// (WMI) can only delay power changes, never the fans. Waits 3 s after start and 8 s after wake so its firmware calls
/// (0x0206, 0x0300) do not pile onto the fan loop's start (ACPI 13 at 22:51:10 on 2026-11-20); polls the power source
/// every 2 s and puts the firmware's CPU limits back when the service stops.
/// </summary>
public sealed class PowerWorker(PowerManager power, PowerSignal signal, FanRuntime runtime, ILogger<PowerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(8);
    private DateTimeOffset? _resumeAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        signal.Changed += OnPower;
        try
        {
            if (!runtime.Simulated) await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(Period);
            do
            {
                try
                {
                    if (_resumeAt is { } at && DateTimeOffset.Now >= at)
                    {
                        _resumeAt = null;
                        power.Reapply();
                    }
                    power.Poll();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Power is a convenience: whatever goes wrong here must never stop the service.
                    logger.LogWarning(ex, "power poll failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally
        {
            signal.Changed -= OnPower;
            // The firmware's CPU limits go back while the service is not running.
            power.RestoreCpuLimit();
        }
    }

    private void OnPower(bool suspending) => _resumeAt = suspending ? null : DateTimeOffset.Now + ResumeDelay;
}
