namespace Excalibur.Core.Fan;

/// <summary>
/// The temperature the curve and the safety floors look at. It may rise at most
/// <see cref="MaxRisePerSecond"/> but follows a fall at once, so a 1-2 s boost spike
/// (a browser waking up: 55 -> 89 C and back) barely moves the fan, while a real load
/// still reaches the curve within seconds. The hot rule in <see cref="SafetyPolicy"/>
/// keeps watching the raw value (see <see cref="FanControlLoop"/>).
/// Measured motivation: docs/07 Aşama C kabul testi.
/// </summary>
public sealed class TemperatureFilter
{
    public const double MaxRisePerSecond = 3.0;

    private double? _cpu, _gpu;
    private DateTimeOffset? _last;

    /// <summary>Returns <paramref name="raw"/> with its temperatures replaced by the filtered ones.</summary>
    public FanSnapshot Apply(FanSnapshot raw, DateTimeOffset now)
    {
        double seconds = _last is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 10) : 0;
        _last = now;
        _cpu = Step(_cpu, raw.CpuTempC, seconds);
        _gpu = Step(_gpu, raw.GpuTempC, seconds);
        return raw with { CpuTempC = (int)Math.Round(_cpu.Value), GpuTempC = (int)Math.Round(_gpu.Value) };
    }

    public void Reset()
    {
        _cpu = _gpu = null;
        _last = null;
    }

    // The first reading after a reset is taken as is: after a restart we must not assume it is cool.
    private static double Step(double? current, int raw, double seconds)
        => current is not { } c || raw <= c ? raw : Math.Min(raw, c + MaxRisePerSecond * seconds);
}
