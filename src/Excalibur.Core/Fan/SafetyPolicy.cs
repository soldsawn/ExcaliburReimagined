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
    private static readonly (int TempC, int MinPercent)[] Floors = [(72, 90), (65, 80)];

    public static FanDecision Apply(FanSnapshot s, FanDecision desired, bool hot, out string? reason)
        => Apply(s, desired, hot, out reason, out _, out _);

    /// <param name="cpuRaised">Set when the CPU fan was raised: <see cref="FanReason.SafetyFloor"/> or <see cref="FanReason.Hot"/>.</param>
    public static FanDecision Apply(FanSnapshot s, FanDecision desired, bool hot, out string? reason,
        out FanReason? cpuRaised, out FanReason? gpuRaised)
    {
        reason = null;
        var cpu = Limit(FanId.Cpu, s, desired.Cpu, hot, ref reason, out cpuRaised);
        var gpu = Limit(FanId.Gpu, s, desired.Gpu, hot, ref reason, out gpuRaised);
        return new FanDecision(cpu, gpu);
    }

    private static FanTarget Limit(FanId fan, FanSnapshot s, FanTarget desired, bool hot, ref string? reason, out FanReason? raised)
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
        reason ??= $"safety: {fan} {percent}% -> {min}% (CPU {s.CpuTempC}C, GPU {s.GpuTempC}C{(hot ? ", hot" : "")})";
        return FanTarget.FromPercent(min);
    }
}

/// <summary>
/// Decides the hot rule from RAW temperatures: either chip at <see cref="HotC"/> or above for
/// <see cref="SustainSeconds"/> in a row, or at <see cref="ImmediateC"/> at any moment.
/// A 1-2 s boost spike to ~89 C (measured) therefore does not slam both fans to 100%.
/// </summary>
public sealed class HotTracker
{
    public const int HotC = 80;
    public const int ImmediateC = 92;
    public const double SustainSeconds = 3;

    private DateTimeOffset? _hotSince;

    public bool Update(FanSnapshot raw, DateTimeOffset now)
    {
        int hottest = Math.Max(raw.CpuTempC, raw.GpuTempC);
        if (hottest < HotC)
        {
            _hotSince = null;
            return false;
        }
        _hotSince ??= now;
        return hottest >= ImmediateC || (now - _hotSince.Value).TotalSeconds >= SustainSeconds - 0.5;
    }

    public void Reset() => _hotSince = null;
}
