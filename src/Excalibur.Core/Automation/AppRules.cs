using Excalibur.Core.Power;

namespace Excalibur.Core.Automation;

/// <summary>A rule's CPU limit: <see cref="Limit"/> in force, or null = no limit of our own (the power mode decides).</summary>
public sealed record CpuLimitOverride(CpuLimit? Limit);

/// <summary>
/// What runs while a program is open (docs/09 A6). <see cref="Program"/> is a file name such as "cs2.exe"; every other field is
/// optional, null = leave it alone. The tray applies <see cref="RefreshHz"/> itself and sends the rest as an <see cref="AppOverride"/>.
/// </summary>
public sealed record AppRule(string Program, string? FanProfile = null, PowerMode? PowerMode = null, int? RefreshHz = null,
    CpuLimitOverride? CpuLimit = null)
{
    public const int MaxRules = 32;

    public AppOverride ToOverride() => new(Program, FanProfile, PowerMode, CpuLimit);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsProgramName(Program)) errors.Add($"'{Program}' is not a program file name");
        if (PowerMode is { } m && !Enum.IsDefined(m)) errors.Add($"unknown power mode '{m}'");
        if (RefreshHz is < 30 or > 360) errors.Add($"refresh rate {RefreshHz} Hz is out of range");
        if (CpuLimit?.Limit is { } limit) errors.AddRange(limit.Validate());
        return errors;
    }

    /// <summary>A bare file name ending in .exe, no folder.</summary>
    public static bool IsProgramName(string? name) =>
        name is { Length: > 4 and <= 260 } && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['\\', '/', ':']) < 0 && name.Trim() == name;
}

/// <summary>
/// The service's part of the running program's rule, sent with <c>setAppOverride</c>. The service holds it in memory only: it is
/// layered over the saved choices and never saved, so they come back when the program closes.
/// </summary>
public sealed record AppOverride(string Program, string? Profile = null, PowerMode? PowerMode = null, CpuLimitOverride? CpuLimit = null)
{
    public IReadOnlyList<string> Validate() => new AppRule(Program, Profile, PowerMode, null, CpuLimit).Validate();
}

/// <summary>
/// Which rule is in force, from the programs running now: the one whose program started last; when it closes, the one before
/// it. Programs already running when tracking starts count in the order of the rules.
/// </summary>
public sealed class AppRuleTracker
{
    private readonly List<string> _started = []; // programs with a rule, in the order they were seen starting

    public AppRule? Active { get; private set; }

    /// <param name="running">File names of the running programs, compared ignoring case.</param>
    /// <returns>True when <see cref="Active"/> changed (another rule, or the same program's rule edited).</returns>
    public bool Update(IReadOnlyList<AppRule> rules, IReadOnlySet<string> running)
    {
        _started.RemoveAll(p => !running.Contains(p) || !rules.Any(r => Same(r.Program, p)));
        foreach (var rule in rules)
            if (running.Contains(rule.Program) && !_started.Any(p => Same(p, rule.Program)))
                _started.Add(rule.Program);
        var next = _started.Count == 0 ? null : rules.First(r => Same(r.Program, _started[^1]));
        bool changed = next != Active;
        Active = next;
        return changed;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
