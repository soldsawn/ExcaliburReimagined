using Excalibur.Core.Power;

namespace Excalibur.FanService;

/// <summary>
/// Runs the power logic beside the fan loop, on its own timer: a slow or stuck firmware call
/// (WMI) can only delay power changes, never the fans. Polls the power source every 2 s and
/// applies the saved mode again 5 s after the laptop wakes from sleep.
/// </summary>
public sealed class PowerWorker(PowerManager power, PowerSignal signal, ILogger<PowerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    private DateTimeOffset? _resumeAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        signal.Changed += OnPower;
        try
        {
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
        }
    }

    private void OnPower(bool suspending) => _resumeAt = suspending ? null : DateTimeOffset.Now + ResumeDelay;
}
