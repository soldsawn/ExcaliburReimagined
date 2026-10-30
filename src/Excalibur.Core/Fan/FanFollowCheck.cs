namespace Excalibur.Core.Fan;

/// <summary>What to do about a written fan target that the fan has had time to follow.</summary>
public enum FanFollow { None, Rewrite, Confirm }

/// <summary>
/// Checks a written fan target by the fan's RPM instead of reading the register back (every EC
/// read can collide with Windows' own EC driver). <see cref="Delay"/> after a write the fan must be
/// within <see cref="Tolerance"/> points of the target, or at least half way there from where it
/// started; otherwise the target is written once more, and if the fan still does not follow, the
/// caller confirms the register with one EC read. Cooling Boost runs the fans at full speed
/// whatever the target, so nothing is checked while it is on. A full target is followed from
/// <see cref="TopSpeed"/> up: the EC holds a fan at 100 around 93–95% (2026-10-29/30 hot starts read
/// 0xC3 and 0x2C for it, and the 0xC3 read fell on five ACPI 15 warnings at 13:33:00.943).
/// </summary>
public sealed class FanFollowCheck
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    public const int Tolerance = 5;
    public const int TopSpeed = 90;

    private sealed record Pending(FanTarget Target, int StartPercent, TimeSpan At, bool Rewritten);
    private readonly Pending?[] _pending = new Pending?[2];

    /// <summary>A target was written; <paramref name="startPercent"/> is where the fan ran just before.</summary>
    public void Wrote(FanId fan, FanTarget target, int startPercent, TimeSpan now)
        => _pending[(int)fan] = target.IsAuto ? null : new Pending(target, startPercent, now, false);

    /// <summary>Called on every reading; says whether to write the target again or confirm it on the EC.</summary>
    public FanFollow Check(FanId fan, int appliedPercent, bool coolingBoost, TimeSpan now, out FanTarget target)
    {
        var p = _pending[(int)fan];
        target = p?.Target ?? FanTarget.Auto;
        if (p is null) return FanFollow.None;
        if (coolingBoost) { _pending[(int)fan] = null; return FanFollow.None; }
        if (now - p.At < Delay) return FanFollow.None;

        int want = p.Target.Percent, way = want - p.StartPercent;
        bool close = Math.Abs(appliedPercent - want) <= Tolerance || want >= 100 - Tolerance && appliedPercent >= TopSpeed;
        bool halfWay = way != 0 && 2 * (appliedPercent - p.StartPercent) * Math.Sign(way) >= Math.Abs(way);
        if (close || halfWay) { _pending[(int)fan] = null; return FanFollow.None; }
        if (!p.Rewritten) { _pending[(int)fan] = p with { At = now, Rewritten = true }; return FanFollow.Rewrite; }
        _pending[(int)fan] = null;
        return FanFollow.Confirm;
    }
}
