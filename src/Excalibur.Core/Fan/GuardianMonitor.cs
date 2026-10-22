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
    /// <summary>
    /// Consecutive hot evaluations before the guardian overrides a live service. The fan percent comes
    /// from the RPM, which trails a new target by a few seconds, so this spans about 8 s.
    /// </summary>
    public int HotEvaluations { get; init; } = 5;
    /// <summary>When hot, a fan running below this percent while the service holds the fans counts as the service misbehaving.</summary>
    public int HotMinPercent { get; init; } = 90;
    /// <summary>The guardian cannot see the targets, so after overriding a live service it waits this long before doing it again.</summary>
    public TimeSpan HotCooldown { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The guardian's decision logic, separate from its host so it can be tested. The guardian
/// never writes anything but 0xFF (EC automatic), and normally reads nothing from the EC: it
/// sees the firmware's WMI sensors and the service's heartbeat. It acts when:
/// <list type="number">
///   <item>the fan service is not alive, i.e. its heartbeat sequence stopped changing: hand the
///   fans back once, without reading the targets first (the EC never undoes a target by itself,
///   docs/06 persist-test, and 0xFF is the safe direction). A failed hand-back is retried every
///   evaluation until <see cref="HandedBack"/> is called;</item>
///   <item>the service is alive and holds the fans, but a chip stays very hot while a fan runs
///   below <see cref="GuardianOptions.HotMinPercent"/>: the service is misbehaving.</item>
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
    private bool _handedBack;
    private TimeSpan? _lastHandBack;

    /// <param name="heartbeat">The last heartbeat file contents, or null if missing.</param>
    /// <param name="sensors">A fresh WMI sensor reading, or null if it could not be read.</param>
    /// <param name="heartbeatProcessAlive">Whether the process id in the heartbeat still exists.</param>
    /// <param name="now">Monotonic time.</param>
    public GuardianVerdict Evaluate(HeartbeatRecord? heartbeat, FanSensors? sensors, bool heartbeatProcessAlive, TimeSpan now)
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

        if (!alive)
        {
            _hotCount = 0;
            return _handedBack
                ? new GuardianVerdict(GuardianAction.None, null, false)
                : new GuardianVerdict(GuardianAction.ResetToAuto, $"the fan service is not alive (quiet {quiet.TotalSeconds:F0} s)", false);
        }
        _handedBack = false;

        if (sensors is null || heartbeat is not { HoldsFans: true })
        {
            _hotCount = 0;
            return new GuardianVerdict(GuardianAction.None, null, true);
        }

        bool hot = sensors.CpuTempC >= _o.CpuHotC || sensors.GpuTempC >= _o.GpuHotC;
        _hotCount = hot ? _hotCount + 1 : 0;
        bool slow = sensors.CpuAppliedPercent < _o.HotMinPercent || sensors.GpuAppliedPercent < _o.HotMinPercent;
        bool cooledDown = _lastHandBack is not { } last || now - last >= _o.HotCooldown;
        if (_hotCount >= _o.HotEvaluations && slow && cooledDown)
        {
            _hotCount = 0;
            return new GuardianVerdict(GuardianAction.ResetToAuto,
                $"CPU {sensors.CpuTempC}C / GPU {sensors.GpuTempC}C while the service runs the fans at {sensors.CpuAppliedPercent}% / {sensors.GpuAppliedPercent}%", true);
        }
        return new GuardianVerdict(GuardianAction.None, null, true);
    }

    /// <summary>The host calls this after a hand-back succeeded.</summary>
    public void HandedBack(TimeSpan now)
    {
        _handedBack = true;
        _lastHandBack = now;
    }
}
