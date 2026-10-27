namespace Excalibur.Core.Fan;

public enum FanLoopMode
{
    /// <summary>Applying the selected profile.</summary>
    Running,
    /// <summary>Unknown EC firmware: reads only, never writes a percent (except during a user-approved <see cref="EcTrial"/>).</summary>
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
    int? GpuCurvePercent = null,
    EcTrialStatus? Trial = null,
    bool EcSeenElsewhere = false,
    FanWritePath WritePath = FanWritePath.Ec);

public sealed record FanLoopOptions
{
    /// <summary>EC firmware versions whose 0x2C/0x2D semantics were verified on hardware.</summary>
    public IReadOnlySet<string> KnownEcVersions { get; init; } = new HashSet<string> { "P008B0" };
    /// <summary>
    /// Unverified versions whose trial passed on someone else's machine (a tester's report): the trial is still run here,
    /// worded as a short calibration. One that passes on 2-3 different machines moves to <see cref="KnownEcVersions"/>.
    /// </summary>
    /// P009C0: two G870s (2026-10-21 a tester's CPU fan; 2026-10-25 a user's trial, both fans followed).
    public IReadOnlySet<string> SeenElsewhereEcVersions { get; init; } = new HashSet<string> { "P003K0", "P009C0" };
    /// <summary>
    /// EC versions the built-in curves suit (tuned on the G870's P008B0); a machine with another one starts on EC automatic after its
    /// trial. P009C0 has only been seen on G870s, the same laptop and fans.
    /// </summary>
    public IReadOnlySet<string> CurveEcVersions { get; init; } = new HashSet<string> { "P008B0", "P009C0" };
    /// <summary>Trusted EC versions whose fans are written through the BIOS (WMI SET 0x0205): their trial passed that way.</summary>
    public IReadOnlySet<string> WmiEcVersions { get; init; } = new HashSet<string>();
    public int MaxReadFailures { get; init; } = 3;
    public int MaxWriteFailures { get; init; } = 2;
    /// <summary>Smaller changes than this are not written (the EC ramps anyway).</summary>
    public int MinWriteDelta { get; init; } = 4;
    /// <summary>
    /// The shortest time between two writes to the same fan: every EC write can collide with Windows'
    /// own EC driver. A safety raise, a user's change and the hand-over to or from the EC are not held back.
    /// </summary>
    public TimeSpan MinWriteInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A rise this big is written without waiting for <see cref="MinWriteInterval"/> (a load that just started).</summary>
    public int UrgentRise { get; init; } = 10;
    public TimeSpan MaxManualDuration { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long after a start the curve, floors and hot rule ignore short spikes (<see cref="Fan.StartGrace"/>).</summary>
    public TimeSpan StartGrace { get; init; } = Fan.StartGrace.Length;
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
    // The verified versions plus any the user's trial passed while running.
    private readonly HashSet<string> _known = new((options ?? new FanLoopOptions()).KnownEcVersions, StringComparer.Ordinal);
    private readonly HashSet<string> _wmi = new((options ?? new FanLoopOptions()).WmiEcVersions, StringComparer.Ordinal);
    private EcTrial? _trial;
    private readonly FanController _controller = new();
    private readonly TemperatureFilter _filter = new();
    private readonly HotTracker _hot = new();
    private readonly FloorTracker _floors = new();
    private readonly StartGrace _grace = new((options ?? new FanLoopOptions()).StartGrace);
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
    private readonly DateTimeOffset?[] _lastWrite = new DateTimeOffset?[2];
    private bool _userChanged;

    public const int MaxEvents = 50;

    /// <summary>
    /// The lowest percent written through WMI (the EC path keeps <see cref="SafetyPolicy.AbsoluteFloor"/>): Casper's G870 app
    /// sends 35-100 there; whether a BIOS takes less is unknown (user's choice, 2026-10-26).
    /// </summary>
    public const int WmiFloor = 30;

    /// <summary>
    /// An EC version passed the user's trial; the host saves it so the next start trusts it too, with the profile the loop
    /// now runs and the way the fans are written. Raised under the loop's lock.
    /// </summary>
    public event Action<string, FanProfile, FanWritePath>? EcTrusted;

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
            _profileChangedBy, _profileChangedAt, _cpuReason, _gpuReason, _cpuCurve, _gpuCurve, _trial?.Status,
            _mode == FanLoopMode.MonitorOnly && _ecVersion is not null && _options.SeenElsewhereEcVersions.Contains(_ecVersion),
            hardware.WritePath);
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
            bool known = _ecVersion is not null && _known.Contains(_ecVersion);
            hardware.WritePath = known && _wmi.Contains(_ecVersion!) ? FanWritePath.Wmi : FanWritePath.Ec;
            // An unverified EC keeps the look before writing (0xFF is written only when not already there).
            ForceAuto(now, "start", atStart: known);
            _grace.Begin(now);
            _mode = known ? FanLoopMode.Running : FanLoopMode.MonitorOnly;
            Log(now, _mode == FanLoopMode.Running
                ? $"started, EC {_ecVersion}, profile {_profile.Name}"
                : $"EC firmware '{_ecVersion ?? "unreadable"}' is not verified; monitor only, fans stay on the EC");
            if (hardware.WritePath == FanWritePath.Wmi) Log(now, "fans written through WMI 0x0205");
            Publish();
        }
    }

    public void Stop(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_trial?.Status.State == EcTrialState.Running) Log(now, "EC trial stopped");
            _trial?.Abort();
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
            // The fans keep their level: a lower curve eases down like a falling temperature (a charger plugged in
            // switched Quiet to Balanced and the fans dropped from 95% to 46% on a stale reading, then rose again).
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

    /// <summary>
    /// Starts the user-approved <see cref="EcTrial"/> of an unverified EC firmware; only in <see cref="FanLoopMode.MonitorOnly"/>
    /// with a readable version and a reading. Passing it trusts the version and starts the profile.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not in MonitorOnly, no version or no reading yet.</exception>
    public void StartEcTrial(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_mode != FanLoopMode.MonitorOnly || _ecVersion is null || _snapshot is null)
                throw new InvalidOperationException("An EC trial needs an unverified, readable EC firmware and a reading.");
            if (_trial?.Status.State == EcTrialState.Running) return;
            _trial = new EcTrial(hardware, _ecVersion, _snapshot, now);
            Log(now, _trial.Status.State == EcTrialState.Failed
                ? $"EC trial of {_ecVersion} not started: too hot (CPU {_snapshot.CpuTempC}C, GPU {_snapshot.GpuTempC}C)"
                : $"EC trial of {_ecVersion} started (user approved)");
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
                if (_mode == FanLoopMode.MonitorOnly) AdvanceTrial(null, now);
                _readFailures++;
                if (_readFailures == _options.MaxReadFailures && _mode == FanLoopMode.Running)
                    Fault(now, $"{_readFailures} unreadable snapshots ({ex.Message})");
                return Publish();
            }

            if (_mode != FanLoopMode.Running)
            {
                if (_mode == FanLoopMode.MonitorOnly) AdvanceTrial(s, now);
                return Publish();
            }

            // Curve and floors see the rise-limited temperature; the hot rule sees the raw one. Just after a start
            // both see only what has lasted (StartGrace).
            bool grace = _grace.Active(now);
            var filtered = _filter.Apply(grace ? _grace.Apply(s, now) : s, now);
            bool hot = _hot.Update(s, now);
            if (grace) hot = _grace.Hot.Update(s, now);
            var desired = _controller.Decide(filtered, _profile, now);
            var decision = SafetyPolicy.Apply(_floors.Apply(filtered, now), desired, hot, out string? reason, out var cpuRaised, out var gpuRaised);
            if (hardware.WritePath == FanWritePath.Wmi)
                decision = new FanDecision(AtLeast(decision.Cpu, WmiFloor), AtLeast(decision.Gpu, WmiFloor));
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
                bool urgent = _userChanged || (fan == FanId.Cpu ? cpuRaised : gpuRaised) is not null;
                if (!NeedsWrite(fan, s.TargetOf(fan), want, urgent, now)) continue;
                try
                {
                    hardware.SetTarget(fan, want);
                    _lastWrite[(int)fan] = now;
                }
                catch (FanBusyException)
                {
                    // Nothing was sent, so not a failure; the cached target is unchanged and a later tick writes it.
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
            _userChanged = false;
            _applied = decision;
            _holdsFans = !decision.Cpu.IsAuto || !decision.Gpu.IsAuto;
            return Publish();
        }
    }

    private void AdvanceTrial(FanSnapshot? s, DateTimeOffset now)
    {
        if (_trial is null) return;
        var before = _trial.Status;
        var after = _trial.Advance(s, now);
        _holdsFans = _trial.HoldsFan;
        if (after == before) return;
        if (after.WmiFollows == false && before.WmiFollows is null) Log(now, "EC trial: the fans do not follow WMI 0x0205; trying the EC");
        if (after.CpuFollows == true && before.CpuFollows != true) Log(now, "EC trial: the CPU fan follows 0x2C");
        if (after.State == EcTrialState.Failed)
            Log(now, $"EC trial of {after.EcVersion} failed ({after.Problem}); fans handed to the EC");
        else if (after.State == EcTrialState.Passed)
        {
            // The built-in curves were tuned on the G870: a model that runs hotter at idle (the G770 at 64-72 C) sat on
            // their steep part, louder than its own EC. Its user picks a profile or a curve of their own.
            if (!_options.CurveEcVersions.Contains(after.EcVersion))
            {
                _profile = FanProfile.EcAuto;
                _beforeManual = null;
                _manualUntil = null;
                Changed(now, Ipc.FanSource.Service);
            }
            Log(now, after.Path == FanWritePath.Wmi
                ? $"EC trial: both fans follow WMI 0x0205; {after.EcVersion} trusted (fans through WMI), profile {_profile.Name} starts"
                : $"EC trial: the GPU fan follows 0x2D; {after.EcVersion} trusted, profile {_profile.Name} starts");
            _known.Add(after.EcVersion);
            if (after.Path == FanWritePath.Wmi) _wmi.Add(after.EcVersion);
            _mode = FanLoopMode.Running;
            ResetSmoothing();
            _userChanged = true;
            EcTrusted?.Invoke(after.EcVersion, _profile, after.Path);
        }
    }

    private static FanTarget AtLeast(FanTarget t, int percent) => t.IsAuto || t.Percent >= percent ? t : FanTarget.FromPercent(percent);

    private bool NeedsWrite(FanId fan, FanTarget current, FanTarget want, bool urgent, DateTimeOffset now)
    {
        if (want.IsAuto || current.IsAuto) return want != current;
        if (want.Percent == 100 && current.Percent != 100) return true;
        if (urgent) return want != current;
        if (want.Percent - current.Percent >= _options.UrgentRise) return true;
        if (_lastWrite[(int)fan] is { } last && now - last < _options.MinWriteInterval) return false;
        return Math.Abs(want.Percent - current.Percent) >= _options.MinWriteDelta;
    }

    private void Fault(DateTimeOffset now, string why)
    {
        _mode = FanLoopMode.Faulted;
        Log(now, $"FAULT: {why}; handing fans to the EC");
        ForceAuto(now, "fault");
    }

    private void ForceAuto(DateTimeOffset now, string why, bool atStart = false)
    {
        ResetSmoothing();
        try
        {
            if (atStart) hardware.ResetToAutoAtStart();
            else hardware.ResetToAuto();
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
        _userChanged = true;
    }

    private void ResetSmoothing()
    {
        _controller.Reset();
        _filter.Reset();
        _hot.Reset();
        _floors.Reset();
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
