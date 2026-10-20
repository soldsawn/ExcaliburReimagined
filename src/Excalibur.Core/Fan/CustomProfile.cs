namespace Excalibur.Core.Fan;

/// <summary>
/// A user-made fan profile (docs/09 A4): a name and a CPU curve, plus a GPU curve when it differs
/// (null = the GPU fan follows the same curve). The name is both the key and what the user reads, so
/// it must be unique and must not look like a built-in profile. The safety floors always apply on top
/// (<see cref="SafetyPolicy"/>), so a custom curve can never make the laptop unsafe; it just cannot ask
/// for less than <see cref="SafetyPolicy.AbsoluteFloor"/>.
/// </summary>
public sealed record CustomProfile(string Name, IReadOnlyList<CurvePoint> Cpu, IReadOnlyList<CurvePoint>? Gpu = null)
{
    public const int MaxNameLength = 24, MaxPoints = 12, MaxProfiles = 16;
    public const int MinTempC = 20, MaxTempC = 110;

    /// <summary>Names a custom profile may not take: the built-in keys and the labels the apps show for them.</summary>
    private static readonly string[] Reserved =
        ["EcAuto", "Quiet", "Balanced", "Performance", "Manual", "Sessiz", "Dengeli", "Performans", "Orijinal ayar", "Orijinal", "Manuel"];

    // Value equality over the point lists (a record would compare the lists by reference).
    public bool Equals(CustomProfile? other) =>
        other is not null && Name == other.Name && Cpu.SequenceEqual(other.Cpu)
        && (Gpu is null ? other.Gpu is null : other.Gpu is not null && Gpu.SequenceEqual(other.Gpu));

    public override int GetHashCode() => HashCode.Combine(Name, Cpu.Count, Gpu?.Count);

    public static bool IsReserved(string name) => Reserved.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Problems that make this profile unusable; empty when it is fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        string name = Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("the profile needs a name");
        else if (name.Length > MaxNameLength) errors.Add($"the name is longer than {MaxNameLength} characters");
        else if (name.Any(char.IsControl)) errors.Add("the name has control characters");
        else if (IsReserved(name)) errors.Add($"'{name}' is the name of a built-in profile");
        CheckCurve("CPU", Cpu, errors);
        if (Gpu is not null) CheckCurve("GPU", Gpu, errors);
        return errors;
    }

    private static void CheckCurve(string label, IReadOnlyList<CurvePoint>? points, List<string> errors)
    {
        if (points is null || points.Count == 0) { errors.Add($"the {label} curve needs at least one point"); return; }
        if (points.Count > MaxPoints) { errors.Add($"the {label} curve has more than {MaxPoints} points"); return; }
        foreach (var p in points)
        {
            if (p.TempC is < MinTempC or > MaxTempC) { errors.Add($"{label}: {p.TempC} °C is outside {MinTempC}-{MaxTempC} °C"); return; }
            if (p.Percent < SafetyPolicy.AbsoluteFloor || p.Percent > 100)
            {
                errors.Add($"{label}: {p.Percent}% is outside {SafetyPolicy.AbsoluteFloor}-100% (the fan never runs slower than {SafetyPolicy.AbsoluteFloor}%)");
                return;
            }
        }
        try { _ = new FanCurve(points); }
        catch (ArgumentException ex) { errors.Add($"{label}: {ex.Message.Split(" (Parameter")[0]}"); }
    }

    /// <summary>The runtime profile. Call only after <see cref="Validate"/> found nothing.</summary>
    public FanProfile ToProfile()
    {
        var cpu = new FanCurve(Cpu);
        return new FanProfile(Name.Trim(), cpu, Gpu is null ? cpu : new FanCurve(Gpu));
    }

    /// <summary>The same profile with a cleaned name and no GPU curve when it equals the CPU one (what gets stored).</summary>
    public CustomProfile Normalized() => this with { Name = Name.Trim(), Gpu = Gpu is { } g && g.SequenceEqual(Cpu) ? null : Gpu };
}
