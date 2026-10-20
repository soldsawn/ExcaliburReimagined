namespace Excalibur.Core.Fan;

public enum GuardianAction { None, ResetToAuto }

public readonly record struct GuardianVerdict(GuardianAction Action, string? Reason, bool ServiceAlive);

public sealed record GuardianOptions
{
    /// <summary>The heartbeat must change at least this often for the service to count as alive.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Shorter grace when the heartbeat's process no longer exists.</summary>
    public TimeSpan StaleAfterProcessGone { get; init; } = TimeSpan.FromSeconds(3);
    public int CpuHotC { get; init; } = 90;
    public int GpuHotC { get; init; } = 88;
    /// <summary>Consecutive hot evaluations before the guardian overrides a live service.</summary>
    public int HotEvaluations { get; init; } = 3;
    /// <summary>When hot, a held fan below this percent counts as the service misbehaving.</summary>
    public int HotMinPercent { get; init; } = 90;
}

/// <summary>
/// The guardian's decision logic, separate from its host so it can be tested. The guardian
/// never writes anything but 0xFF (EC automatic). It acts when:
/// <list type="number">
///   <item>a fan target is held (EC 0x2C/0x2D not 0xFF) and the fan service is not alive,
///   i.e. its heartbeat sequence stopped changing. The EC never undoes a target by itself
///   (docs/06 persist-test), so this is the crash case;</item>
///   <item>the EC cannot be read and the service is not alive: reset blindly, 0xFF is the safe direction;</item>
///   <item>the service is alive but a chip stays very hot while a fan is held below
///   <see cref="GuardianOptions.HotMinPercent"/>: the service is misbehaving.</item>
/// </list>
/// Time is a monotonic value from the host (Stopwatch), so clock changes do not matter.
/// </summary>
public sealed class GuardianMonitor(GuardianOptions? options = null)
{
    private readonly GuardianOptions _o = options ?? new GuardianOptions();
    private long? _lastSequence;
    private TimeSpan _lastChange;
    private bool _started;
    private int _hotCount;

    /// <param name="heartbeat">The last heartbeat file contents, or null if missing.</param>
    /// <param name="snapshot">A fresh EC/WMI snapshot, or null if it could not be read.</param>
    /// <param name="heartbeatProcessAlive">Whether the process id in the heartbeat still exists.</param>
    /// <param name="now">Monotonic time.</param>
    public GuardianVerdict Evaluate(HeartbeatRecord? heartbeat, FanSnapshot? snapshot, bool heartbeatProcessAlive, TimeSpan now)
    {
        if (!_started)
        {
            // Give an already running service one full window before judging it.
            _started = true;
            _lastSequence = heartbeat?.Sequence;
            _lastChange = now;
        }
        else if (heartbeat?.Sequence != _lastSequence)
        {
            _lastSequence = heartbeat?.Sequence;
            _lastChange = now;
        }

        var quiet = now - _lastChange;
        bool alive = heartbeat is not null
            && quiet < _o.StaleAfter
            && (heartbeatProcessAlive || quiet < _o.StaleAfterProcessGone);

        if (snapshot is null)
        {
            _hotCount = 0;
            return alive
                ? new GuardianVerdict(GuardianAction.None, null, true)
                : new GuardianVerdict(GuardianAction.ResetToAuto, "EC unreadable and the fan service is not alive", false);
        }

        bool held = !snapshot.CpuTarget.IsAuto || !snapshot.GpuTarget.IsAuto;
        if (!held)
        {
            _hotCount = 0;
            return new GuardianVerdict(GuardianAction.None, null, alive);
        }
        if (!alive)
        {
            _hotCount = 0;
            return new GuardianVerdict(GuardianAction.ResetToAuto,
                $"fan targets held (CPU {snapshot.CpuTarget}, GPU {snapshot.GpuTarget}) but the fan service is not alive (quiet {quiet.TotalSeconds:F0} s)", false);
        }

        bool hot = snapshot.CpuTempC >= _o.CpuHotC || snapshot.GpuTempC >= _o.GpuHotC;
        _hotCount = hot ? _hotCount + 1 : 0;
        if (_hotCount >= _o.HotEvaluations && (HeldBelow(snapshot.CpuTarget) || HeldBelow(snapshot.GpuTarget)))
        {
            _hotCount = 0;
            return new GuardianVerdict(GuardianAction.ResetToAuto,
                $"CPU {snapshot.CpuTempC}C / GPU {snapshot.GpuTempC}C while the service holds CPU {snapshot.CpuTarget}, GPU {snapshot.GpuTarget}", true);
        }
        return new GuardianVerdict(GuardianAction.None, null, true);
    }

    private bool HeldBelow(FanTarget t) => !t.IsAuto && t.Percent < _o.HotMinPercent;
}
