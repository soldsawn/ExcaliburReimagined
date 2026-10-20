namespace Excalibur.Core.Fan;

/// <summary>
/// A named pair of curves (CPU fan follows the CPU, GPU fan the GPU). <see cref="EcAuto"/>
/// is special: it never writes, leaving both fans on the stock EC curve.
/// Built-in curves come from the stock measurement in docs/07 (A.2). <see cref="IsManual"/>
/// profiles are taken literally: no cross-chip support (the safety floors still apply).
/// </summary>
public sealed record FanProfile(string Name, FanCurve CpuCurve, FanCurve GpuCurve, bool IsEcAuto = false, bool IsManual = false)
{
    public FanCurve CurveOf(FanId fan) => fan == FanId.Cpu ? CpuCurve : GpuCurve;

    public static FanProfile EcAuto { get; } = new("EcAuto", FanCurve.Flat(0), FanCurve.Flat(0), IsEcAuto: true);

    public static FanProfile Quiet { get; } = Same("Quiet", FanCurve.Of((50, 25), (55, 30), (60, 45), (65, 70), (70, 90), (75, 100)));

    public static FanProfile Balanced { get; } = Same("Balanced", FanCurve.Of((50, 30), (55, 40), (60, 60), (65, 85), (70, 95), (75, 100)));

    public static FanProfile Performance { get; } = Same("Performance", FanCurve.Of((50, 45), (55, 60), (60, 80), (65, 100)));

    public static IReadOnlyList<FanProfile> BuiltIn { get; } = [EcAuto, Quiet, Balanced, Performance];

    /// <summary>Fixed percents, used for the time-limited manual mode.</summary>
    public static FanProfile Manual(int cpuPercent, int gpuPercent)
        => new("Manual", FanCurve.Flat(cpuPercent), FanCurve.Flat(gpuPercent), IsManual: true);

    public static FanProfile? FindBuiltIn(string name)
        => BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static FanProfile Same(string name, FanCurve curve) => new(name, curve, curve);
}
