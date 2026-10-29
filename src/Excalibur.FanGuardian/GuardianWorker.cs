using System.Diagnostics;
using System.ServiceProcess;
using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;

namespace Excalibur.FanGuardian;

/// <summary>
/// Every 2 s: reads the fan service's heartbeat and the sensors it carries (else the firmware's WMI sensors, no EC read), asks
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
        EcTrace.Dump("stop", DateTime.Now, EcTrace.DumpedAtStop);
    }

    private void Check()
    {
        var heartbeat = FanHeartbeat.Read(FanHeartbeat.DefaultPath);
        bool processAlive = heartbeat is { } hb && ProcessExists(hb.ProcessId);

        FanSensors? sensors = null;
        // A new heartbeat carries what the service read this second: no firmware call of our own, so the two processes
        // never stack calls on the EC (every ACPI 13 of 2026-10-25/26 came when one did). Only when the service reads
        // nothing (its reads fail, it is stopping) does the guardian ask the firmware itself, and only while the service
        // holds the fans: otherwise the monitor does not look at the sensors (the read at a start stalled 4.2 s, 10-26 22:46).
        bool newBeat = heartbeat is { } beat && beat.Sequence != _lastBeat;
        _lastBeat = heartbeat?.Sequence;
        bool fromService = newBeat && heartbeat!.Value.Sensors is not null;
        if (fromService)
        {
            sensors = heartbeat!.Value.Sensors;
            _lastSensors = sensors;
            _sinceSensors.Restart();
        }
        else if (heartbeat is { HoldsFans: true } && SensorsDue())
        {
            try { sensors = hardware.ReadSensors(); }
            catch (FanReadException) { } // the monitor treats null as "unknown"
            _lastSensors = sensors;
            _sinceSensors.Restart();
        }
        if (sensors is not null && fromService != _sensorsFromService)
        {
            Log(fromService ? "sensors: from the fan service's heartbeat" : "sensors: reading the firmware (the fan service sends none)");
            _sensorsFromService = fromService;
        }

        var now = _clock.Elapsed;
        var verdict = _monitor.Evaluate(heartbeat, sensors, processAlive, now);

        if (_wasAlive != verdict.ServiceAlive)
        {
            Log(verdict.ServiceAlive ? "fan service is alive" : "fan service is NOT alive");
            _wasAlive = verdict.ServiceAlive;
        }

        if (verdict.Action == GuardianAction.ResetToAuto)
        {
            try
            {
                // The way the service writes them (the last heartbeat says): WMI fans may not answer the EC's registers.
                hardware.WritePath = heartbeat is { WmiFans: true } ? FanWritePath.Wmi : FanWritePath.Ec;
                hardware.ResetToAutoUnread();
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

    // The sensors matter only near the hot limits (88-90 C), so while the last reading was cool they are read this
    // seldom: the fan service already reads the block every 2 s, and each read is a firmware call that stalled for
    // 4 s now and then, with an ACPI 13 warning after it (2026-10-26 00:09, 00:28).
    private static readonly TimeSpan CoolSensorPeriod = TimeSpan.FromSeconds(10);
    private const int WatchfulC = 80;
    private FanSensors? _lastSensors;
    private readonly Stopwatch _sinceSensors = new();
    private long? _lastBeat;
    private bool? _sensorsFromService;

    private bool SensorsDue() =>
        _lastSensors is not { } last || !_sinceSensors.IsRunning
        || last.CpuTempC >= WatchfulC || last.GpuTempC >= WatchfulC
        || _sinceSensors.Elapsed >= CoolSensorPeriod - TimeSpan.FromMilliseconds(500); // a 2-s tick's jitter

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
