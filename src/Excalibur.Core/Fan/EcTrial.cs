namespace Excalibur.Core.Fan;

public enum EcTrialState { Running, Passed, Failed }

/// <summary>Why a trial failed; stable codes the UI words itself.</summary>
public static class EcTrialProblem
{
    /// <summary>A chip was too warm to start, or got too warm during the trial.</summary>
    public const string TooHot = "too_hot";
    /// <summary>The fan's RPM did not move toward the written target.</summary>
    public const string NoFollow = "no_follow";
    /// <summary>A write (or handing the fan back) failed.</summary>
    public const string WriteFailed = "write_failed";
    /// <summary>The sensors could not be read.</summary>
    public const string ReadFailed = "read_failed";
    /// <summary>The service stopped or the machine went to sleep during the trial.</summary>
    public const string Stopped = "stopped";
}

/// <param name="Fan">The fan being tried on the EC while <see cref="State"/> is Running; null during the WMI step.</param>
/// <param name="CpuFollows">Null until the CPU fan was tried on the EC.</param>
/// <param name="WmiFollows">Null until both fans were tried through WMI (SET 0x0205).</param>
/// <param name="Path">How the fans are written from now on; meaningful once <see cref="State"/> is Passed.</param>
public sealed record EcTrialStatus(
    EcTrialState State, string EcVersion, FanId? Fan, bool? CpuFollows, bool? GpuFollows, string? Problem, DateTimeOffset StartedAt,
    bool? WmiFollows = null, FanWritePath Path = FanWritePath.Ec);

/// <summary>
/// The user-approved check of an EC firmware we have not verified. First through the BIOS (WMI SET 0x0205, both fans at
/// once): if both follow, the fans are written that way and the EC's registers are never touched. Otherwise the EC:
/// does 0x2C move the CPU fan and 0x2D the GPU fan?
/// Each try gets a target <see cref="Step"/> points away from where the fan runs, must cover two thirds of
/// that way by RPM within <see cref="Timeout"/>, and is handed back (0xFF) before the next.
/// Only starts below <see cref="MaxStartC"/> and stops above <see cref="AbortC"/>; any failure hands both fans
/// back. <see cref="FanControlLoop"/> drives it once per tick.
/// </summary>
public sealed class EcTrial
{
    public const int Step = 30;
    public const int MaxStartC = 75;
    public const int AbortC = 80;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IFanHardware _hw;
    private FanId _fan = FanId.Cpu;
    private int _start;
    private FanTarget? _target;
    private DateTimeOffset _writtenAt;
    private bool _wmiStep = true;
    private int _gpuStart;
    private FanTarget? _gpuTarget;

    public EcTrialStatus Status { get; private set; }

    /// <summary>True while one of our targets may be in a fan register.</summary>
    public bool HoldsFan => _target is not null || _gpuTarget is not null;

    public EcTrial(IFanHardware hw, string ecVersion, FanSnapshot s, DateTimeOffset now)
    {
        _hw = hw;
        Status = new EcTrialStatus(EcTrialState.Running, ecVersion, null, null, null, null, now);
        if (Math.Max(s.CpuTempC, s.GpuTempC) >= MaxStartC) Status = Status with { State = EcTrialState.Failed, Problem = EcTrialProblem.TooHot };
        else _hw.WritePath = FanWritePath.Wmi;
    }

    /// <summary>One tick: write the target, or check the fan's progress. <paramref name="s"/> is null when the read failed.</summary>
    public EcTrialStatus Advance(FanSnapshot? s, DateTimeOffset now)
    {
        if (Status.State != EcTrialState.Running)
        {
            if (_target is not null) HandBack(); // a failed hand-back is tried again every tick
            return Status;
        }
        if (s is null) return Fail(EcTrialProblem.ReadFailed);
        if (Math.Max(s.CpuTempC, s.GpuTempC) >= AbortC) return Fail(EcTrialProblem.TooHot);
        if (_wmiStep) return AdvanceWmi(s, now);

        int percent = _fan == FanId.Cpu ? s.CpuAppliedPercent : s.GpuAppliedPercent;
        if (_target is not { } target)
        {
            _start = percent;
            var want = Away(percent);
            try { _hw.SetTarget(_fan, want); }
            catch (FanBusyException) { return Status; } // nothing written; try again next tick
            catch (FanWriteException) { return Fail(EcTrialProblem.WriteFailed); }
            _target = want;
            _writtenAt = now;
            return Status;
        }

        bool follows = Follows(percent, _start, target);
        if (!follows && now - _writtenAt < Timeout) return Status;
        if (!follows) return Fail(EcTrialProblem.NoFollow);

        try { _hw.SetTarget(_fan, FanTarget.Auto); }
        catch (Exception ex) when (ex is FanBusyException or FanWriteException) { return Fail(EcTrialProblem.WriteFailed); }
        _target = null;
        if (_fan == FanId.Cpu)
        {
            _fan = FanId.Gpu;
            return Status = Status with { Fan = FanId.Gpu, CpuFollows = true };
        }
        return Status = Status with { State = EcTrialState.Passed, Fan = null, GpuFollows = true, Path = FanWritePath.Ec };
    }

    /// <summary>Both fans through WMI: they pass together, or the trial goes on to the EC.</summary>
    private EcTrialStatus AdvanceWmi(FanSnapshot s, DateTimeOffset now)
    {
        if (_target is not { } cpu || _gpuTarget is not { } gpu)
        {
            (_start, _gpuStart) = (s.CpuAppliedPercent, s.GpuAppliedPercent);
            var (wantCpu, wantGpu) = (Away(_start), Away(_gpuStart));
            try
            {
                _target = wantCpu; // set first: a write that reports a failure may still have landed
                _hw.SetTarget(FanId.Cpu, wantCpu);
                _gpuTarget = wantGpu;
                _hw.SetTarget(FanId.Gpu, wantGpu);
            }
            catch (Exception ex) when (ex is FanBusyException or FanWriteException) { return ToEc(); } // no WMI fan control here
            _writtenAt = now;
            return Status;
        }

        bool follows = Follows(s.CpuAppliedPercent, _start, cpu) && Follows(s.GpuAppliedPercent, _gpuStart, gpu);
        if (!follows && now - _writtenAt < Timeout) return Status;
        if (!follows) return ToEc();
        try { _hw.ResetToAuto(); }
        catch (FanWriteException) { return Fail(EcTrialProblem.WriteFailed); }
        _target = _gpuTarget = null;
        return Status = Status with { State = EcTrialState.Passed, WmiFollows = true, Path = FanWritePath.Wmi };
    }

    /// <summary>WMI did not move the fans: hand them back that way, then try the EC from the next tick.</summary>
    private EcTrialStatus ToEc()
    {
        try { _hw.ResetToAuto(); }
        catch (FanWriteException) { } // nothing to undo when WMI does not work here
        _target = _gpuTarget = null;
        _wmiStep = false;
        _hw.WritePath = FanWritePath.Ec;
        return Status = Status with { Fan = FanId.Cpu, WmiFollows = false };
    }

    private static FanTarget Away(int percent) => FanTarget.FromPercent(percent <= 100 - Step ? percent + Step : percent - Step);

    private static bool Follows(int percent, int start, FanTarget target)
    {
        int way = target.Percent - start;
        return 3 * (percent - start) * Math.Sign(way) >= 2 * Math.Abs(way);
    }

    /// <summary>Stops the trial (service stopping, suspend): both fans back to the EC.</summary>
    public void Abort()
    {
        if (Status.State == EcTrialState.Running) Fail(EcTrialProblem.Stopped);
    }

    private EcTrialStatus Fail(string problem)
    {
        bool? failed = problem == EcTrialProblem.NoFollow ? false : null;
        Status = _wmiStep ? Status with { State = EcTrialState.Failed, Problem = problem }
            : _fan == FanId.Cpu
            ? Status with { State = EcTrialState.Failed, Fan = null, CpuFollows = failed, Problem = problem }
            : Status with { State = EcTrialState.Failed, Fan = null, GpuFollows = failed, Problem = problem };
        _target ??= FanTarget.Auto; // a write may have landed even when it reported a failure
        HandBack();
        return Status;
    }

    private void HandBack()
    {
        try
        {
            _hw.ResetToAuto();
            _target = _gpuTarget = null;
            _hw.WritePath = FanWritePath.Ec; // only a failed trial hands back: the EC path is where an unverified EC stays
        }
        catch (FanWriteException) { } // HoldsFan stays true, so the guardian keeps watching
    }
}
