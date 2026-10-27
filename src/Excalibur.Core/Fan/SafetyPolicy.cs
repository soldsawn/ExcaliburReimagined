namespace Excalibur.Core.Fan;

/// <summary>
/// Hard limits applied after <see cref="FanController"/>; they can only RAISE a fan.
/// Thresholds come from the stock measurement (docs/07 A.2): the EC itself runs the
/// fans at 86-95% from 60-65 C, and never above ~95% (CPU) / 84% (GPU), so at high
/// temperatures we go to 100% instead of handing back to the EC, which would slow them.
/// The floors read the filtered snapshot; the hot rule (both fans 100%) is decided by the
/// caller from raw temperatures, see <see cref="HotTracker"/>.
/// Auto targets (EcAuto profile, fault fallback) pass through untouched.
/// </summary>
public static class SafetyPolicy
{
    public const int AbsoluteFloor = 25;
    // 70/78 C since 2026-10-23 (were 65/72, chosen with the slow WMI reading): the CPU's own sensor sits at 54-62 C
    // idle and 94-98 C under full load (TjMax 100), and a floor at 65 C made Quiet hunt between its curve and 80%.
    private static readonly (int TempC, int MinPercent)[] Floors = [(78, 90), (70, 80)];

    public static FanDecision Apply(FanSnapshot s, FanDecision desired, bool hot, out string? reason)
        => Apply(s, desired, hot, out reason, out _, out _);

    /// <param name="cpuRaised">Set when the CPU fan was raised: <see cref="FanReason.SafetyFloor"/> or <see cref="FanReason.Hot"/>.</param>
    /// <param name="raw">The temperatures the hot rule decided on, for its log line (the floors see a held value: a
    /// hot line read "CPU 60C" while the CPU sat at 97 C, 2026-10-27).</param>
    public static FanDecision Apply(FanSnapshot s, FanDecision desired, bool hot, out string? reason,
        out FanReason? cpuRaised, out FanReason? gpuRaised, FanSnapshot? raw = null)
    {
        reason = null;
        var shown = hot && raw is not null ? raw : s;
        var cpu = Limit(FanId.Cpu, s, shown, desired.Cpu, hot, ref reason, out cpuRaised);
        var gpu = Limit(FanId.Gpu, s, shown, desired.Gpu, hot, ref reason, out gpuRaised);
        return new FanDecision(cpu, gpu);
    }

    private static FanTarget Limit(FanId fan, FanSnapshot s, FanSnapshot shown, FanTarget desired, bool hot, ref string? reason, out FanReason? raised)
    {
        raised = null;
        if (desired.IsAuto) return desired;

        int percent = desired.Percent, min = AbsoluteFloor;
        if (hot) min = 100;
        else
        {
            int own = s.TempOf(fan);
            foreach (var (t, floor) in Floors)
                if (own >= t) { min = floor; break; }
        }
        if (percent >= min) return desired;

        raised = hot ? FanReason.Hot : FanReason.SafetyFloor;
        reason ??= $"safety: {fan} {percent}% -> {min}% (CPU {shown.CpuTempC}C, GPU {shown.GpuTempC}C{(hot ? ", hot" : "")})";
        return FanTarget.FromPercent(min);
    }
}

/// <summary>
/// Decides the hot rule from RAW temperatures: either chip at <see cref="HotC"/> or above for
/// <see cref="SustainSeconds"/> in a row, or at <see cref="ImmediateC"/> on <see cref="ImmediateReadings"/> readings in a row.
/// A 1-2 s boost spike to ~89 C (measured) therefore does not slam both fans to 100%, nor does a single
/// MSR reading of 92-100 C: the CPU's own sensor jumps that far for a fraction of a second (2026-10-23).
/// </summary>
public sealed class HotTracker
{
    public const int HotC = 80;
    public const int ImmediateC = 92;
    public const double SustainSeconds = 3;
    public const int ImmediateReadings = 2;

    private readonly int _hotC;
    private readonly double _sustainSeconds;
    private readonly int? _immediateC;
    private DateTimeOffset? _hotSince;
    private int _immediate;

    /// <param name="immediateC">Null: only a sustained temperature is hot (<see cref="StartGrace"/>).</param>
    public HotTracker(int hotC = HotC, double sustainSeconds = SustainSeconds, int? immediateC = ImmediateC)
    {
        _hotC = hotC;
        _sustainSeconds = sustainSeconds;
        _immediateC = immediateC;
    }

    public bool Update(FanSnapshot raw, DateTimeOffset now)
    {
        int hottest = Math.Max(raw.CpuTempC, raw.GpuTempC);
        if (hottest < _hotC)
        {
            _hotSince = null;
            _immediate = 0;
            return false;
        }
        _hotSince ??= now;
        _immediate = hottest >= _immediateC ? _immediate + 1 : 0;
        return _immediate >= ImmediateReadings || (now - _hotSince.Value).TotalSeconds >= _sustainSeconds - 0.5;
    }

    public void Reset()
    {
        _hotSince = null;
        _immediate = 0;
    }
}

/// <summary>
/// The temperature the floors look at: the lowest filtered reading of the last <see cref="SustainSeconds"/>, so a
/// floor engages only once its threshold has held that long. A 1-2 s boost spike crossing 70 C sent the CPU fan from
/// its curve to 80% and back ~300 times a day (G870 and G770, 2026-10-23/24); a real load still reaches the floor
/// within seconds, and the hot rule keeps watching the raw value.
/// </summary>
public sealed class FloorTracker
{
    public const double SustainSeconds = 3;

    private readonly Queue<(DateTimeOffset At, int CpuC, int GpuC)> _recent = new();

    public FanSnapshot Apply(FanSnapshot filtered, DateTimeOffset now)
    {
        _recent.Enqueue((now, filtered.CpuTempC, filtered.GpuTempC));
        while (now - _recent.Peek().At > TimeSpan.FromSeconds(SustainSeconds)) _recent.Dequeue();
        return filtered with { CpuTempC = _recent.Min(r => r.CpuC), GpuTempC = _recent.Min(r => r.GpuC) };
    }

    public void Reset() => _recent.Clear();
}

/// <summary>
/// The first <see cref="Length"/> after the service starts or the machine wakes. Windows starting up takes the CPU to
/// 80-97 C for 7-23 s, and the curve, the floors and the hot rule ran both fans at 90-100% at nearly every start
/// (2026-10-24..27; a tester's G870 found it loud enough to stop using the app). Meanwhile the curve and the floors see
/// the lowest reading of the last <see cref="SustainSeconds"/> (no more than <see cref="SeedC"/>, the CPU's idle, until
/// that much has been read), and the hot rule needs <see cref="HotGraceC"/> for <see cref="HotGraceSeconds"/>: under
/// the guardian's ~8 s at 90 C, so it never finds the fans slow first (user's choice, 2026-10-27).
/// </summary>
public sealed class StartGrace
{
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(60);
    public const double SustainSeconds = 10;
    public const int SeedC = 60;
    public const int HotGraceC = 90;
    public const double HotGraceSeconds = 6;

    private readonly TimeSpan _length;
    private readonly Queue<(DateTimeOffset At, int CpuC, int GpuC)> _recent = new();
    private DateTimeOffset? _startedAt;

    public StartGrace(TimeSpan? length = null) => _length = length ?? Length;

    public HotTracker Hot { get; } = new(HotGraceC, HotGraceSeconds, immediateC: null);

    public void Begin(DateTimeOffset now)
    {
        _startedAt = now;
        _recent.Clear();
        Hot.Reset();
    }

    public bool Active(DateTimeOffset now) => _startedAt is { } at && now - at < _length;

    /// <summary>Returns <paramref name="raw"/> with the temperatures held for <see cref="SustainSeconds"/>.</summary>
    public FanSnapshot Apply(FanSnapshot raw, DateTimeOffset now)
    {
        _recent.Enqueue((now, raw.CpuTempC, raw.GpuTempC));
        while (now - _recent.Peek().At > TimeSpan.FromSeconds(SustainSeconds)) _recent.Dequeue();
        bool spanned = now - _startedAt >= TimeSpan.FromSeconds(SustainSeconds);
        int Held(int lowest) => spanned ? lowest : Math.Min(lowest, SeedC);
        return raw with { CpuTempC = Held(_recent.Min(r => r.CpuC)), GpuTempC = Held(_recent.Min(r => r.GpuC)) };
    }
}
