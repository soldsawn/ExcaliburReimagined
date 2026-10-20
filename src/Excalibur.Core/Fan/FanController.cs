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
    /// <summary>Raised to a safety floor (25% always, 80% from 65 C, 90% from 72 C).</summary>
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
///   <item>rising demand is applied at once; falling demand is evaluated
///   <see cref="HysteresisC"/> hotter and may drop at most <see cref="MaxDropPerSecond"/>
///   per second, so the fan does not hunt around a curve point;</item>
///   <item>Cooling Boost (Fn+1) means both fans at 100% (user decision, docs/07).</item>
/// </list>
/// </summary>
public sealed class FanController
{
    public const int SupportOffset = 10;
    public const int HysteresisC = 3;
    public const double MaxDropPerSecond = 2.5;

    // Smoothed level per fan, null until the first decision (or after EcAuto).
    private readonly double?[] _level = new double?[2];
    private readonly FanReason[] _reason = new FanReason[2];

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
            FanTarget.FromPercent(Step(FanId.Cpu, snapshot, profile, seconds)),
            FanTarget.FromPercent(Step(FanId.Gpu, snapshot, profile, seconds)));
    }

    public void Reset()
    {
        _level[0] = _level[1] = null;
        _reason[0] = _reason[1] = FanReason.Auto;
        _lastTick = null;
    }

    private int Step(FanId fan, FanSnapshot s, FanProfile profile, double seconds)
    {
        var (demand, demandReason) = Demand(fan, s, profile, 0);
        int i = (int)fan;
        if (_level[i] is not { } level || demand >= level)
        {
            _level[i] = demand;
            _reason[i] = demandReason;
            return demand;
        }
        // Falling: only as far as the curve says when read HysteresisC hotter, and no faster than the slew limit.
        int relaxed = Math.Min((int)Math.Round(level), Demand(fan, s, profile, HysteresisC).Percent);
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
