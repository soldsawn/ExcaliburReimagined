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

/// <param name="Fan">The fan being tried while <see cref="State"/> is Running.</param>
/// <param name="CpuFollows">Null until the CPU fan was tried.</param>
public sealed record EcTrialStatus(
    EcTrialState State, string EcVersion, FanId? Fan, bool? CpuFollows, bool? GpuFollows, string? Problem, DateTimeOffset StartedAt);

/// <summary>
/// The user-approved check of an EC firmware we have not verified: does 0x2C move the CPU fan and 0x2D the GPU fan?
/// Each fan in turn gets a target <see cref="Step"/> points away from where it runs, must cover two thirds of
/// that way by RPM within <see cref="Timeout"/>, and is handed back to the EC (0xFF, read back) before the next.
/// Only starts below <see cref="MaxStartC"/> and stops above <see cref="AbortC"/>; any failure hands both fans
/// back. <see cref="FanControlLoop"/> drives it once per tick.
/// </summary>
public sealed class EcTrial
{
    public const int Step = 30;
    public const int MaxStartC = 60;
    public const int AbortC = 70;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IFanHardware _hw;
    private FanId _fan = FanId.Cpu;
    private int _start;
    private FanTarget? _target;
    private DateTimeOffset _writtenAt;

    public EcTrialStatus Status { get; private set; }

    /// <summary>True while one of our targets may be in a fan register.</summary>
    public bool HoldsFan => _target is not null;

    public EcTrial(IFanHardware hw, string ecVersion, FanSnapshot s, DateTimeOffset now)
    {
        _hw = hw;
        Status = new EcTrialStatus(EcTrialState.Running, ecVersion, FanId.Cpu, null, null, null, now);
        if (Math.Max(s.CpuTempC, s.GpuTempC) >= MaxStartC) Status = Status with { State = EcTrialState.Failed, Fan = null, Problem = EcTrialProblem.TooHot };
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

        int percent = _fan == FanId.Cpu ? s.CpuAppliedPercent : s.GpuAppliedPercent;
        if (_target is not { } target)
        {
            _start = percent;
            var want = FanTarget.FromPercent(percent <= 100 - Step ? percent + Step : percent - Step);
            try { _hw.SetTarget(_fan, want); }
            catch (FanBusyException) { return Status; } // nothing written; try again next tick
            catch (FanWriteException) { return Fail(EcTrialProblem.WriteFailed); }
            _target = want;
            _writtenAt = now;
            return Status;
        }

        int way = target.Percent - _start;
        bool follows = 3 * (percent - _start) * Math.Sign(way) >= 2 * Math.Abs(way);
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
        return Status = Status with { State = EcTrialState.Passed, Fan = null, GpuFollows = true };
    }

    /// <summary>Stops the trial (service stopping, suspend): both fans back to the EC.</summary>
    public void Abort()
    {
        if (Status.State == EcTrialState.Running) Fail(EcTrialProblem.Stopped);
    }

    private EcTrialStatus Fail(string problem)
    {
        bool? failed = problem == EcTrialProblem.NoFollow ? false : null;
        Status = _fan == FanId.Cpu
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
            _target = null;
        }
        catch (FanWriteException) { } // HoldsFan stays true, so the guardian keeps watching
    }
}
