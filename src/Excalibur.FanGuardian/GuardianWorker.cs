using System.Diagnostics;
using System.ServiceProcess;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;

namespace Excalibur.FanGuardian;

/// <summary>
/// Every 2 s: reads the fan service's heartbeat and the firmware's WMI sensors (no EC read), asks
/// <see cref="GuardianMonitor"/> what to do, and if needed hands both fans back to the
/// EC (0xFF, the only value the guardian ever writes). When the service looks dead it
/// also tries to start it again (at most once a minute; the service recovery settings
/// normally do that first).
/// </summary>
public sealed class GuardianWorker(EcFanHardware hardware, FanFileLog log, ILogger<GuardianWorker> logger) : BackgroundService
{
    public const string FanServiceName = "ExcaliburFan";
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartEvery = TimeSpan.FromMinutes(1);

    private readonly GuardianMonitor _monitor = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool? _wasAlive;
    private TimeSpan? _lastRestartAttempt;
    private string? _lastResetReason;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log("guardian started");
        using var timer = new PeriodicTimer(Period);
        try
        {
            do Check();
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        Log("guardian stopped");
    }

    private void Check()
    {
        var heartbeat = FanHeartbeat.Read(FanHeartbeat.DefaultPath);
        bool processAlive = heartbeat is { } hb && ProcessExists(hb.ProcessId);

        FanSensors? sensors = null;
        try { sensors = hardware.ReadSensors(); }
        catch (FanReadException) { } // the monitor treats null as "unknown"

        var now = _clock.Elapsed;
        var verdict = _monitor.Evaluate(heartbeat, sensors, processAlive, now);

        if (_wasAlive != verdict.ServiceAlive)
        {
            Log(verdict.ServiceAlive ? "fan service is alive" : "fan service is NOT alive");
            _wasAlive = verdict.ServiceAlive;
        }

        // Right after Windows starts the fans are on EC automatic anyway; no raw EC write then (EcQuiet.AfterBoot).
        if (verdict.Action == GuardianAction.ResetToAuto && EcQuiet.BootRemaining == TimeSpan.Zero)
        {
            try
            {
                hardware.ResetToAuto();
                _monitor.HandedBack(now);
                // Say it once per distinct reason, not every 2 s while a target keeps reappearing.
                if (verdict.Reason != _lastResetReason) Log($"RESET fans to EC automatic: {verdict.Reason}");
                _lastResetReason = verdict.Reason;
            }
            catch (Exception ex)
            {
                Log($"!! could not reset the fans ({verdict.Reason}): {ex.Message}");
            }
        }
        else
        {
            _lastResetReason = null;
        }

        if (!verdict.ServiceAlive && (_lastRestartAttempt is not { } last || now - last >= RestartEvery))
        {
            _lastRestartAttempt = now;
            TryStartFanService();
        }
    }

    private void TryStartFanService()
    {
        try
        {
            using var sc = new ServiceController(FanServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped) return;
            if (sc.StartType == ServiceStartMode.Disabled) return; // stopped on purpose
            sc.Start();
            Log("started the fan service");
        }
        catch (InvalidOperationException) { } // not installed (console testing)
        catch (Exception ex) { logger.LogWarning(ex, "could not start the fan service"); }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Log(string message)
    {
        log.Write(DateTimeOffset.Now, message);
        logger.LogInformation("{Message}", message);
    }
}
