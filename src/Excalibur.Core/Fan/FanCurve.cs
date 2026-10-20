namespace Excalibur.Core.Fan;

public readonly record struct CurvePoint(int TempC, int Percent);

/// <summary>
/// A temperature -> fan percent curve: linear between points, flat beyond the ends.
/// Points must be sorted by temperature and never decrease in percent, so a hotter chip
/// can never get a slower fan.
/// </summary>
public sealed class FanCurve
{
    public IReadOnlyList<CurvePoint> Points { get; }

    public FanCurve(IEnumerable<CurvePoint> points)
    {
        var list = points.ToList();
        if (list.Count == 0) throw new ArgumentException("A curve needs at least one point.", nameof(points));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Percent is < 0 or > 100)
                throw new ArgumentException($"Point {i}: percent {list[i].Percent} is outside 0-100.", nameof(points));
            if (i > 0 && list[i].TempC <= list[i - 1].TempC)
                throw new ArgumentException($"Point {i}: temperatures must strictly increase.", nameof(points));
            if (i > 0 && list[i].Percent < list[i - 1].Percent)
                throw new ArgumentException($"Point {i}: percent must not decrease as temperature rises.", nameof(points));
        }
        Points = list;
    }

    public static FanCurve Of(params (int TempC, int Percent)[] points)
        => new(points.Select(p => new CurvePoint(p.TempC, p.Percent)));

    public static FanCurve Flat(int percent) => Of((0, percent));

    public int Evaluate(int tempC)
    {
        var p = Points;
        if (tempC <= p[0].TempC) return p[0].Percent;
        if (tempC >= p[^1].TempC) return p[^1].Percent;
        int i = 1;
        while (p[i].TempC < tempC) i++;
        var (lo, hi) = (p[i - 1], p[i]);
        double t = (double)(tempC - lo.TempC) / (hi.TempC - lo.TempC);
        return (int)Math.Round(lo.Percent + t * (hi.Percent - lo.Percent), MidpointRounding.AwayFromZero);
    }
}
