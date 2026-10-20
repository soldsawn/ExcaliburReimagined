namespace Excalibur.Core.Fan;

public enum FanLoopMode
{
    /// <summary>Applying the selected profile.</summary>
    Running,
    /// <summary>Unknown EC firmware: reads only, never writes a percent.</summary>
    MonitorOnly,
    /// <summary>Too many read/write failures: fans handed to the EC until <see cref="FanControlLoop.ClearFault"/>.</summary>
    Faulted,
    /// <summary>Stopped; fans handed to the EC.</summary>
    Stopped,
}

public sealed record FanEvent(DateTimeOffset At, string Message);

public sealed record FanStatus(
    FanLoopMode Mode,
    string Profile,
    DateTimeOffset? ManualUntil,
    FanSnapshot? Snapshot,
    FanDecision? Applied,
    string? EcVersion,
    int ConsecutiveReadFailures,
    bool HoldsFans,
    string? ProfileChangedBy = null,
    DateTimeOffset? ProfileChangedAt = null,
    FanReason CpuReason = FanReason.Auto,
    FanReason GpuReason = FanReason.Auto,
    int? CpuCurvePercent = null,
    int? GpuCurvePercent = null);

public sealed record FanLoopOptions
{
    /// <summary>EC firmware versions whose 0x2C/0x2D semantics were verified on hardware.</summary>
    public IReadOnlySet<string> KnownEcVersions { get; init; } = new HashSet<string> { "P008B0" };
    public int MaxReadFailures { get; init; } = 3;
    public int MaxWriteFailures { get; init; } = 2;
    /// <summary>Smaller changes than this are not written (the EC ramps anyway).</summary>
    public int MinWriteDelta { get; init; } = 2;
    public TimeSpan MaxManualDuration { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The fan service's brain, independent of how it is hosted: call <see cref="Start"/>,
/// then <see cref="Tick"/> about once a second, and <see cref="Stop"/> on shutdown.
/// The safe state is always "EC automatic" (0xFF): we go there on start, stop, on
/// repeated read failures, on repeated write failures, and for unknown EC firmware.
/// The EC does not undo our writes by itself (docs/06 persist-test), so a separate
/// guardian must also watch <see cref="FanStatus.HoldsFans"/>.
/// </summary>
public sealed class FanControlLoop(IFanHardware hardware, FanLoopOptions? options = null)
{
    private readonly FanLoopOptions _options = options ?? new FanLoopOptions();
    private readonly FanController _controller = new();
    private readonly TemperatureFilter _filter = new();
    private readonly HotTracker _hot = new();
    private readonly LinkedList<FanEvent> _events = new();
    private readonly object _gate = new();
    // Events and the published status have their own cheap guards so a pipe request never waits
    // behind a slow EC read that holds _gate inside Tick.
    private readonly object _eventsGate = new();
    private volatile FanStatus? _published;

    private FanProfile _profile = FanProfile.Balanced;
    private FanProfile? _beforeManual;
    private DateTimeOffset? _manualUntil;
    private FanLoopMode _mode = FanLoopMode.Stopped;
    private FanSnapshot? _snapshot;
    private FanDecision? _applied;
    private string? _ecVersion;
    private int _readFailures, _writeFailures;
    private bool _holdsFans;
    private long _totalEvents;
    private string? _safetyReason;
    private FanReason _cpuReason, _gpuReason;
    private int? _cpuCurve, _gpuCurve;
    private string? _profileChangedBy;
    private DateTimeOffset? _profileChangedAt;

    public const int MaxEvents = 50;

    /// <summary>How many events were ever logged; with <see cref="Events"/> a host can tell which are new.</summary>
    public long TotalEvents
    {
        get { lock (_eventsGate) return _totalEvents; }
    }

    /// <summary>The state as of the end of the last tick or command; never blocks on the EC.</summary>
    public FanStatus Status
    {
        get
        {
            var published = _published;
            if (published is not null) return published;
            lock (_gate) return Publish();
        }
    }

    private FanStatus Publish()
    {
        var status = new FanStatus(_mode, _profile.Name, _manualUntil, _snapshot, _applied, _ecVersion, _readFailures, _holdsFans,
            _profileChangedBy, _profileChangedAt, _cpuReason, _gpuReason, _cpuCurve, _gpuCurve);
        _published = status;
        return status;
    }

    public IReadOnlyList<FanEvent> Events
    {
        get { lock (_eventsGate) return _events.ToList(); }
    }

    public void Start(FanProfile profile, DateTimeOffset now)
    {
        lock (_gate)
        {
            _profile = profile;
            _ecVersion = hardware.ReadEcVersion();
            ForceAuto(now, "start");
            _mode = _ecVersion is not null && _options.KnownEcVersions.Contains(_ecVersion)
                ? FanLoopMode.Running
                : FanLoopMode.MonitorOnly;
            Log(now, _mode == FanLoopMode.Running
                ? $"started, EC {_ecVersion}, profile {_profile.Name}"
                : $"EC firmware '{_ecVersion ?? "unreadable"}' is not verified; monitor only, fans stay on the EC");
            Publish();
        }
    }

    public void Stop(DateTimeOffset now)
    {
        lock (_gate)
        {
            ForceAuto(now, "stop");
            _mode = FanLoopMode.Stopped;
            Publish();
        }
    }

    /// <param name="source">Who asked (one of <c>FanSource</c>); reported in the status and the event log.</param>
    public void SetProfile(FanProfile profile, DateTimeOffset now, string? source = null)
    {
        lock (_gate)
        {
            _profile = profile;
            _beforeManual = null;
            _manualUntil = null;
            ResetSmoothing();
            Changed(now, source);
            Log(now, source is null ? $"profile {profile.Name}" : $"profile {profile.Name} ({source})");
            Publish();
        }
    }

    public void SetManual(int cpuPercent, int gpuPercent, TimeSpan duration, DateTimeOffset now, string? source = null)
    {
        if (duration <= TimeSpan.Zero || duration > _options.MaxManualDuration)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, $"Manual mode lasts 1 s to {_options.MaxManualDuration}.");
        var manual = FanProfile.Manual(cpuPercent, gpuPercent); // validates 0-100
        lock (_gate)
        {
            _beforeManual ??= _profile;
            _profile = manual;
            _manualUntil = now + duration;
            ResetSmoothing();
            Changed(now, source);
            Log(now, $"manual CPU {cpuPercent}% GPU {gpuPercent}% until {_manualUntil:HH:mm:ss}");
            Publish();
        }
    }

    /// <summary>Leaves the Faulted state after the user acknowledged it; the next tick resumes the profile.</summary>
    public void ClearFault(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_mode != FanLoopMode.Faulted) return;
            _readFailures = _writeFailures = 0;
            _mode = FanLoopMode.Running;
            ResetSmoothing();
            Log(now, "fault cleared");
            Publish();
        }
    }

    public FanStatus Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_mode == FanLoopMode.Stopped) return Publish();

            if (_manualUntil is { } until && now >= until)
            {
                _profile = _beforeManual ?? FanProfile.Balanced;
                _beforeManual = null;
                _manualUntil = null;
                ResetSmoothing();
                Changed(now, Ipc.FanSource.Service);
                Log(now, $"manual mode ended, back to {_profile.Name}");
            }

            FanSnapshot s;
            try
            {
                s = hardware.Read();
                _snapshot = s;
                _readFailures = 0;
            }
            catch (FanReadException ex)
            {
                _readFailures++;
                if (_readFailures == _options.MaxReadFailures && _mode == FanLoopMode.Running)
                    Fault(now, $"{_readFailures} unreadable snapshots ({ex.Message})");
                return Publish();
            }

            if (_mode != FanLoopMode.Running) return Publish();

            // Curve and floors see the rise-limited temperature; the hot rule sees the raw one.
            var filtered = _filter.Apply(s, now);
            bool hot = _hot.Update(s, now);
            var desired = _controller.Decide(filtered, _profile, now);
            var decision = SafetyPolicy.Apply(filtered, desired, hot, out string? reason, out var cpuRaised, out var gpuRaised);
            _cpuReason = decision.Cpu.IsAuto ? FanReason.Auto : cpuRaised ?? _controller.ReasonOf(FanId.Cpu);
            _gpuReason = decision.Gpu.IsAuto ? FanReason.Auto : gpuRaised ?? _controller.ReasonOf(FanId.Gpu);
            // What each curve alone asks for, at the temperature the curve sees.
            _cpuCurve = _profile.IsEcAuto ? null : _profile.CpuCurve.Evaluate(filtered.CpuTempC);
            _gpuCurve = _profile.IsEcAuto ? null : _profile.GpuCurve.Evaluate(filtered.GpuTempC);
            // Log the override when it engages and when it lets go, not on every tick it holds.
            if ((reason is null) != (_safetyReason is null))
                Log(now, reason ?? "safety: released");
            _safetyReason = reason;

            // The failure count only resets after a tick in which every write succeeded,
            // so one fan failing every time cannot hide behind the other succeeding.
            bool failedThisTick = false;
            foreach (var fan in new[] { FanId.Cpu, FanId.Gpu })
            {
                var want = decision.Of(fan);
                if (!NeedsWrite(s.TargetOf(fan), want)) continue;
                try
                {
                    hardware.SetTarget(fan, want);
                }
                catch (FanWriteException ex)
                {
                    failedThisTick = true;
                    _writeFailures++;
                    Log(now, ex.Message);
                    if (_writeFailures >= _options.MaxWriteFailures)
                    {
                        Fault(now, $"{_writeFailures} failed writes");
                        return Publish();
                    }
                }
            }
            if (!failedThisTick) _writeFailures = 0;
            _applied = decision;
            _holdsFans = !decision.Cpu.IsAuto || !decision.Gpu.IsAuto;
            return Publish();
        }
    }

    private bool NeedsWrite(FanTarget current, FanTarget want)
    {
        if (want.IsAuto || current.IsAuto) return want != current;
        if (want.Percent == 100 && current.Percent != 100) return true;
        return Math.Abs(want.Percent - current.Percent) >= _options.MinWriteDelta;
    }

    private void Fault(DateTimeOffset now, string why)
    {
        _mode = FanLoopMode.Faulted;
        Log(now, $"FAULT: {why}; handing fans to the EC");
        ForceAuto(now, "fault");
    }

    private void ForceAuto(DateTimeOffset now, string why)
    {
        ResetSmoothing();
        try
        {
            hardware.ResetToAuto();
            _holdsFans = false;
            _applied = FanDecision.BothAuto;
            _cpuReason = _gpuReason = FanReason.Auto;
            _cpuCurve = _gpuCurve = null;
            if (why == "start") Log(now, "fans handed to the EC (start)");
        }
        catch (Exception ex)
        {
            // Keep HoldsFans as it was: the guardian relies on it to retry.
            Log(now, $"!! could not hand fans to the EC on {why}: {ex.Message}");
        }
    }

    private void Changed(DateTimeOffset now, string? source)
    {
        _profileChangedBy = source;
        _profileChangedAt = now;
    }

    private void ResetSmoothing()
    {
        _controller.Reset();
        _filter.Reset();
        _hot.Reset();
    }

    private void Log(DateTimeOffset now, string message)
    {
        lock (_eventsGate)
        {
            _events.AddLast(new FanEvent(now, message));
            _totalEvents++;
            while (_events.Count > MaxEvents) _events.RemoveFirst();
        }
    }
}
