namespace Excalibur.Core.Fan;

public readonly record struct FanDecision(FanTarget Cpu, FanTarget Gpu)
{
    public FanTarget Of(FanId fan) => fan == FanId.Cpu ? Cpu : Gpu;

    public static FanDecision BothAuto { get; } = new(FanTarget.Auto, FanTarget.Auto);
}

/// <summary>
/// Why a fan runs at its current target, for the UI ("why is my GPU fan at 65% at 48 C?").
/// Stored in 4 bits of the history record, so keep it under 16 values and never renumber.
/// </summary>
public enum FanReason : byte
{
    /// <summary>The EC drives the fan (EcAuto profile, stopped, faulted).</summary>
    Auto = 0,
    /// <summary>Its own chip's curve.</summary>
    Curve = 1,
    /// <summary>The other chip's curve minus <see cref="FanController.SupportOffset"/> (shared heat pipes).</summary>
    Support = 2,
    /// <summary>Temperature fell; the fan slows down gradually instead of hunting.</summary>
    Holding = 3,
    /// <summary>Raised to a safety floor (25% always, 80% from 70 C, 90% from 78 C).</summary>
    SafetyFloor = 4,
    /// <summary>The hot rule: both fans 100%.</summary>
    Hot = 5,
    /// <summary>Fn+1 Cooling Boost: both fans 100%.</summary>
    Boost = 6,
    /// <summary>Time-limited manual percent.</summary>
    Manual = 7,
}

/// <summary>
/// Turns a snapshot and a profile into the fan percents to apply (before
/// <see cref="SafetyPolicy"/>). Pure apart from its own smoothing state:
/// <list type="bullet">
///   <item>each fan follows its own chip's curve, but never runs more than
///   <see cref="SupportOffset"/> points below the other chip's curve (shared heat pipes);</item>
///   <item>rising demand is applied once it has lasted <see cref="RiseDelay"/>, and then only as far as the
///   lowest demand in that time: a 1-2 s burst (Windows waking up, 57 -> 66 C and back every 15-20 s,
///   2026-10-23) used to take the fan from 52% to 80% and back. The first decision, Cooling Boost and the
///   safety policy are not delayed;</item>
///   <item>falling demand is evaluated <see cref="HysteresisC"/> hotter, starts only once it has lasted
///   <see cref="FallDelay"/> and may drop at most <see cref="MaxDropPerSecond"/> per second: under load the
///   CPU dips for 3-5 s between bursts (72-75 -> 62-64 C, 2026-10-23), which took the fan from 97% to 82%
///   and back every few seconds. Fast up, slow down, as fan control tools usually do;</item>
///   <item>Cooling Boost (Fn+1) means both fans at 100% (user decision, docs/07).</item>
/// </list>
/// </summary>
public sealed class FanController
{
    public const int SupportOffset = 10;
    public const int HysteresisC = 3;
    public const double MaxDropPerSecond = 2.5;
    public static readonly TimeSpan RiseDelay = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FallDelay = TimeSpan.FromSeconds(10);

    // Smoothed level per fan, null until the first decision (or after EcAuto).
    private readonly double?[] _level = new double?[2];
    private readonly FanReason[] _reason = new FanReason[2];
    // Each fan's recent demands (and the demand read HysteresisC hotter), newest last, kept for FallDelay.
    private readonly Queue<(DateTimeOffset At, int Percent, FanReason Reason, int Relaxed)>[] _recent = [new(), new()];

    /// <summary>Why the last <see cref="Decide"/> chose this fan's percent (before the safety policy).</summary>
    public FanReason ReasonOf(FanId fan) => _reason[(int)fan];
    private DateTimeOffset? _lastTick;

    public FanDecision Decide(FanSnapshot snapshot, FanProfile profile, DateTimeOffset now)
    {
        double seconds = _lastTick is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 10) : 0;
        _lastTick = now;

        if (profile.IsEcAuto)
        {
            Reset();
            return FanDecision.BothAuto;
        }
        if (snapshot.CoolingBoost)
        {
            _level[0] = _level[1] = 100;
            _reason[0] = _reason[1] = FanReason.Boost;
            return new FanDecision(FanTarget.FromPercent(100), FanTarget.FromPercent(100));
        }
        return new FanDecision(
            FanTarget.FromPercent(Step(FanId.Cpu, snapshot, profile, seconds, now)),
            FanTarget.FromPercent(Step(FanId.Gpu, snapshot, profile, seconds, now)));
    }

    public void Reset()
    {
        _level[0] = _level[1] = null;
        _reason[0] = _reason[1] = FanReason.Auto;
        _recent[0].Clear();
        _recent[1].Clear();
        _lastTick = null;
    }

    private int Step(FanId fan, FanSnapshot s, FanProfile profile, double seconds, DateTimeOffset now)
    {
        var (demand, demandReason) = Demand(fan, s, profile, 0);
        int i = (int)fan;
        var recent = _recent[i];
        recent.Enqueue((now, demand, demandReason, Demand(fan, s, profile, HysteresisC).Percent));
        while (recent.Count > 1 && now - recent.Peek().At > FallDelay) recent.Dequeue();
        if (_level[i] is not { } level)
        {
            _level[i] = demand;
            _reason[i] = demandReason;
            return demand;
        }
        if (demand >= level)
        {
            // Rise only to what every demand of the last RiseDelay asked for, once they span it.
            var lowest = recent.Where(r => now - r.At <= RiseDelay).MinBy(r => r.Percent);
            if (now - recent.Peek().At >= RiseDelay - TimeSpan.FromMilliseconds(500) && lowest.Percent > level)
            {
                _level[i] = lowest.Percent;
                _reason[i] = lowest.Reason;
            }
            return (int)Math.Round(_level[i]!.Value, MidpointRounding.AwayFromZero);
        }
        // Falling: only as far as the curve read HysteresisC hotter has said for all of FallDelay, and no
        // faster than the slew limit.
        int relaxed = Math.Min((int)Math.Round(level), recent.Max(r => r.Relaxed));
        double next = Math.Max(relaxed, level - MaxDropPerSecond * seconds);
        _level[i] = next;
        int percent = (int)Math.Round(next, MidpointRounding.AwayFromZero);
        _reason[i] = percent > demand ? FanReason.Holding : demandReason;
        return percent;
    }

    private static (int Percent, FanReason Reason) Demand(FanId fan, FanSnapshot s, FanProfile profile, int bias)
    {
        FanId other = fan == FanId.Cpu ? FanId.Gpu : FanId.Cpu;
        int own = profile.CurveOf(fan).Evaluate(s.TempOf(fan) + bias);
        if (profile.IsManual) return (own, FanReason.Manual);
        int support = profile.CurveOf(other).Evaluate(s.TempOf(other) + bias) - SupportOffset;
        return support > own ? (Math.Clamp(support, 0, 100), FanReason.Support) : (Math.Clamp(own, 0, 100), FanReason.Curve);
    }
}
