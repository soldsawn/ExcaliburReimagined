using Excalibur.Core.Fan;
using Excalibur.Core.Hardware;
using Excalibur.Core.History;
using Excalibur.Core.Settings;

namespace Excalibur.FanService;

/// <summary>
/// Hosts <see cref="FanControlLoop"/>: one tick per second, a heartbeat for the guardian,
/// events to the log file. Fans go back to the EC (0xFF) on stop, on suspend, and when
/// anything unexpected throws; after a resume the loop restarts 5 s later.
/// </summary>
public sealed class FanWorker(
    FanControlLoop loop,
    FanRuntime runtime,
    ServiceSettingsStore settings,
    FanFileLog log,
    HistoryRecorder history,
    PowerSignal power,
    ILogger<FanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    private const int StatusLineEveryTicks = 60;

    private readonly object _powerGate = new();
    private bool _suspended;
    private DateTimeOffset? _resumeAt;
    private long _sequence, _loggedEvents;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        power.Changed += OnPower;
        var profile = SavedProfile();
        loop.Start(profile, DateTimeOffset.Now);
        Log($"service started ({(runtime.Simulated ? "SIMULATION" : "real EC")}), profile {profile.Name}");
        foreach (string problem in settings.LoadProblems) Log($"settings: {problem}");
        DrainEvents();

        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                TickOnce();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Unexpected: hand the fans back first, then let the process die so the
            // service recovery settings restart it (the guardian covers the gap).
            Log($"!! unexpected error, handing fans to the EC: {ex}");
            loop.Stop(DateTimeOffset.Now);
            WriteHeartbeat();
            Environment.ExitCode = 1;
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        power.Changed -= OnPower;
        loop.Stop(DateTimeOffset.Now);
        WriteHeartbeat();
        history.Flush();
        DrainEvents();
        Log("service stopped, fans handed to the EC");
        EcTrace.Dump("stop", DateTime.Now, EcTrace.DumpedAtStop);
    }

    private void TickOnce()
    {
        var now = DateTimeOffset.Now;
        lock (_powerGate)
        {
            if (_suspended) return;
            if (_resumeAt is { } at)
            {
                if (now < at) return;
                _resumeAt = null;
                loop.Start(SavedProfile(), now);
            }
        }

        runtime.Fake?.Advance(Period);
        var status = loop.Tick(now);
        // A new snapshot means this tick read the sensors; an old one (a failed read, the loop stopped) is not passed on.
        var fresh = status.Snapshot is { } read && !ReferenceEquals(read, _lastSnapshot) ? read : null;
        _lastSnapshot = status.Snapshot;
        WriteHeartbeat(fresh is null ? null
            : new FanSensors(fresh.CpuTempC, fresh.GpuTempC, fresh.CpuAppliedPercent, fresh.GpuAppliedPercent), fresh);
        history.Record(status, now);
        DrainEvents();

        if (_sequence % StatusLineEveryTicks == 0 && status.Snapshot is { } s)
            Log($"status {status.Mode} {status.Profile}: CPU {s.CpuTempC}C {s.CpuRpm}rpm {s.CpuTarget}, GPU {s.GpuTempC}C {s.GpuRpm}rpm {s.GpuTarget}");
    }

    private void OnPower(bool suspending)
    {
        lock (_powerGate)
        {
            var now = DateTimeOffset.Now;
            if (suspending)
            {
                _suspended = true;
                _resumeAt = null;
                loop.Stop(now);
                WriteHeartbeat();
                Log("suspending: fans handed to the EC");
            }
            else
            {
                // Windows sends both ResumeSuspend and ResumeAutomatic; the first one already scheduled the restart.
                if (!_suspended && _resumeAt is not null) return;
                _suspended = false;
                _resumeAt = now + ResumeDelay;
                Log($"resumed: fan control restarts at {_resumeAt:HH:mm:ss}");
            }
        }
    }

    private FanProfile SavedProfile() => settings.Current.FindProfile(settings.Current.Profile) ?? FanProfile.Balanced;

    private FanSnapshot? _lastSnapshot;

    private void WriteHeartbeat(FanSensors? sensors = null, FanSnapshot? read = null)
    {
        try
        {
            var status = loop.Status;
            FanHeartbeat.Write(runtime.HeartbeatPath, new HeartbeatRecord(++_sequence, DateTimeOffset.Now, status.HoldsFans,
                Environment.ProcessId, sensors, status.WritePath == FanWritePath.Wmi, read?.CpuTarget, read?.GpuTarget));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "heartbeat write failed");
        }
    }

    private void DrainEvents()
    {
        long total = loop.TotalEvents;
        if (total == _loggedEvents) return;
        var events = loop.Events;
        long firstIndex = total - events.Count;
        foreach (var (e, i) in events.Select((e, i) => (e, firstIndex + i)))
            if (i >= _loggedEvents) log.Write(e.At, e.Message);
        _loggedEvents = total;
    }

    private void Log(string message)
    {
        log.Write(DateTimeOffset.Now, message);
        logger.LogInformation("{Message}", message);
    }
}
