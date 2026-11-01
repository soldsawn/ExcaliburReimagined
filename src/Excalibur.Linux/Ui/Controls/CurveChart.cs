using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Excalibur.App;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;
using Excalibur.Core.History;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>
/// Temperature -> fan % chart, the Windows window's CurveChart in Avalonia: the selected profile's CPU and GPU curves, the
/// safety floors, the stock EC curve (can be hidden in Settings), warm/hot zones, a faded trail of where the fans really ran
/// in the last minutes, and both fans now. Hovering shows what each line says at that temperature; hovering a fan's dot
/// explains why it runs as it does.
/// </summary>
internal sealed class CurveChart : Control
{
    private const int MinC = 30, MaxC = 100, WarmC = 65;
    // The stock EC's CPU-fan steps (see the Windows CurveChart for where they come from); High performance has stronger top steps.
    private static readonly (int C, int P)[] Stock =
        [(MinC, 60), (45, 60), (45, 64), (47, 64), (47, 68), (56, 68), (56, 72), (59, 72), (59, 80), (62, 80), (62, 88), (65, 88), (65, 94), (MaxC, 94)];
    private static readonly (int C, int P)[] StockHigh =
        [(MinC, 60), (45, 60), (45, 64), (47, 64), (47, 68), (56, 68), (56, 72), (59, 72), (59, 84), (62, 84), (62, 94), (65, 94), (65, 100), (MaxC, 100)];
    private (int C, int P)[] StockSteps => _status?.Power?.Mode == Excalibur.Core.Power.PowerMode.HighPerformance ? StockHigh : Stock;
    private static readonly HashSet<string> StockTableEcVersions = new(StringComparer.OrdinalIgnoreCase) { "P008B0", "P009B0" };
    private bool ShowStock => !(this.TryFindResource("StockLine", ActualThemeVariant, out var v) && v is false);
    internal bool ShowsStock => ShowStock;
    internal bool StockIsKnown => _status?.EcVersion is { } ec && StockTableEcVersions.Contains(ec);
    internal string StockLabel => StockIsKnown ? "Orijinal ayar" : "Orijinal ayar (G870 tablosu)";
    // Safety floors: 25% always, 80% from 70 C, 90% from 78 C, 100% when hot (>= 80 C for 3 s).
    internal static readonly (int C, int P)[] Floors =
    [
        (MinC, SafetyPolicy.AbsoluteFloor), (70, SafetyPolicy.AbsoluteFloor), (70, 80), (78, 80),
        (78, 90), (HotTracker.HotC, 90), (HotTracker.HotC, 100), (MaxC, 100),
    ];
    private static int Lowest => SafetyPolicy.AbsoluteFloor;

    private FanProfile? _profile;
    private FanStatusDto? _status;
    private Point? _mouse;
    private Rect _plot;
    // What the dots show: eased toward the latest reading, so they glide instead of jumping every poll.
    private (double C, double P)? _shownCpu, _shownGpu;
    private (double C, double P)? _targetCpu, _targetGpu;
    private (double C, double P)[] _cpuTrail = [], _gpuTrail = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private TimeSpan _lastFrame;

    public CurveChart()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        PointerMoved += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        PointerExited += (_, _) => { _mouse = null; InvalidateVisual(); };
        // About 30 frames a second while the chart is on screen: enough for a smooth glide and a slow pulse.
        _frames.Tick += (_, _) => OnFrame();
        AttachedToVisualTree += (_, _) => _frames.Start();
        DetachedFromVisualTree += (_, _) => _frames.Stop();
    }

    private bool Motion => !(this.TryFindResource("Motion", ActualThemeVariant, out var v) && v is false);

    private void OnFrame()
    {
        var now = _clock.Elapsed;
        double dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        // Motion off in Settings: the dots jump straight to each reading.
        bool still = !Motion;
        double kc = still ? 1 : 1 - Math.Exp(-Math.Min(dt, 0.2) / 1.2), kp = still ? 1 : 1 - Math.Exp(-Math.Min(dt, 0.2) / 0.35);
        _shownCpu = Ease(_shownCpu, _targetCpu, kc, kp);
        _shownGpu = Ease(_shownGpu, _targetGpu, kc, kp);
        if (IsEffectivelyVisible) InvalidateVisual();
    }

    private static (double C, double P)? Ease((double C, double P)? shown, (double C, double P)? target, double kc, double kp) =>
        target is not { } t ? null
        : shown is not { } s ? t
        : (s.C + (t.C - s.C) * kc, s.P + (t.P - s.P) * kp);

    public void Show(FanProfile? profile, FanStatusDto? status)
    {
        _profile = profile;
        _status = status;
        _targetCpu = status?.CpuTempC is { } cc && status.CpuAppliedPercent is { } cp ? (Math.Clamp(cc, MinC, MaxC), cp) : null;
        _targetGpu = status?.GpuTempC is { } gc && status.GpuAppliedPercent is { } gp ? (Math.Clamp(gc, MinC, MaxC), gp) : null;
        InvalidateVisual();
    }

    public void ShowTrail(HistorySeries? trail)
    {
        _cpuTrail = Smooth(trail, p => (p.CpuTempC, p.CpuApplied));
        _gpuTrail = Smooth(trail, p => (p.GpuTempC, p.GpuApplied));
        InvalidateVisual();
    }

    private IBrush Res(string key) => Palette.Brush(this, key);

    private Color ColorOf(string key) => Res(key) is ISolidColorBrush b ? b.Color : Colors.Gray;

    private Point At(double c, double p) => new(
        _plot.Left + (c - MinC) / (MaxC - MinC) * _plot.Width,
        _plot.Bottom - p / 100 * _plot.Height);

    private double TempAt(double x) => MinC + (x - _plot.Left) / _plot.Width * (MaxC - MinC);

    public override void Render(DrawingContext dc)
    {
        // Transparent background so the whole area takes the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        _plot = new Rect(40, 8, Math.Max(0, Bounds.Width - 52), Math.Max(0, Bounds.Height - 32));
        if (_plot.Width < 60 || _plot.Height < 60) return;

        Zones(dc);
        Grid(dc);
        if (ShowStock) Polyline(dc, StockSteps, new Pen(Res("SubText"), 1.5, DashStyle.Dash));
        // Square caps: a dot is a zero-length dash, invisible with flat ones (WPF squares dash ends by default).
        Polyline(dc, Floors, new Pen(Res("Bad"), 1.2, DashStyle.Dot, PenLineCap.Square));
        TrailPath(dc, _gpuTrail, _shownGpu, "Ok");
        TrailPath(dc, _cpuTrail, _shownCpu, "Value");

        if (_profile is { IsEcAuto: false } profile)
        {
            // The same curve for both chips draws once; separate curves draw the GPU one under the CPU one.
            if (!SameCurve(profile)) Curve(dc, profile.GpuCurve, new Pen(Res("Ok"), 2));
            Curve(dc, profile.CpuCurve, new Pen(Res("Accent"), 2.5));
        }

        Point? gpu = Dot(dc, _shownGpu, "Ok");
        Point? cpu = Dot(dc, _shownCpu, "Value");

        if (_mouse is not { } m) return;
        if (Near(cpu, m)) DotTip(dc, FanId.Cpu, cpu!.Value);
        else if (Near(gpu, m)) DotTip(dc, FanId.Gpu, gpu!.Value);
        else if (_plot.Contains(m)) Crosshair(dc, m);
    }

    // ---- layers ----------------------------------------------------------------------------

    private void Zones(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(ColorOf("Warn"), 0.07), null, new Rect(At(WarmC, 100), At(HotTracker.HotC, 0)));
        dc.DrawRectangle(new SolidColorBrush(ColorOf("Bad"), 0.10), null, new Rect(At(HotTracker.HotC, 100), At(MaxC, 0)));
    }

    private void Grid(DrawingContext dc)
    {
        var grid = new Pen(Res("Border"), 1);
        for (int c = MinC; c <= MaxC; c += 10)
        {
            dc.DrawLine(grid, At(c, 0), At(c, 100));
            Label(dc, $"{c}°", At(c, 0) + new Vector(0, 6), Res("SubText"), center: true);
        }
        for (int p = 0; p <= 100; p += 25)
        {
            dc.DrawLine(grid, At(MinC, p), At(MaxC, p));
            Label(dc, $"%{p}", At(MinC, p) + new Vector(-8, -8), Res("SubText"), right: true);
        }
    }

    /// <summary>Averages neighbouring readings so the trail reads as a smooth path rather than a scatter of dots.</summary>
    private static (double C, double P)[] Smooth(HistorySeries? trail, Func<HistoryPoint, (int C, int P)> pick)
    {
        if (trail is null) return [];
        var raw = trail.Points.Where(p => p is not null).Select(p => pick(p!)).ToArray();
        const int half = 3;
        var result = new List<(double, double)>();
        for (int i = 0; i < raw.Length; i += 2)
        {
            int from = Math.Max(0, i - half), to = Math.Min(raw.Length - 1, i + half);
            double c = 0, p = 0;
            for (int j = from; j <= to; j++) { c += raw[j].C; p += raw[j].P; }
            int n = to - from + 1;
            result.Add((Math.Clamp(c / n, MinC, MaxC), p / n));
        }
        return [.. result];
    }

    /// <summary>Where each fan really ran: a smooth path that fades and thins toward the past, ending at the live dot.</summary>
    private void TrailPath(DrawingContext dc, (double C, double P)[] path, (double C, double P)? live, string key)
    {
        var pts = path.Select(p => At(p.C, p.P)).ToList();
        if (live is { } l) pts.Add(At(l.C, l.P));
        if (pts.Count < 2) return;
        var color = ColorOf(key);
        for (int i = 1; i < pts.Count; i++)
        {
            double age = (double)i / (pts.Count - 1); // 0 = oldest, 1 = now
            double fade = age * age;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (0.05 + 0.6 * fade)), color.R, color.G, color.B)),
                1 + 2.2 * fade, lineCap: PenLineCap.Round);
            // Catmull-Rom through the neighbours, drawn one short segment at a time so it can fade.
            Point p0 = pts[Math.Max(0, i - 2)], p1 = pts[i - 1], p2 = pts[i], p3 = pts[Math.Min(pts.Count - 1, i + 1)];
            var c1 = p1 + (p2 - p0) / 6;
            var c2 = p2 - (p3 - p1) / 6;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p1, false);
                ctx.CubicBezierTo(c1, c2, p2);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, g);
        }
    }

    /// <summary>The live reading: a solid core and ring with a soft halo that breathes slowly.</summary>
    private Point? Dot(DrawingContext dc, (double C, double P)? shown, string key)
    {
        if (shown is not { } s) return null;
        var at = At(s.C, s.P);
        var brush = Res(key);
        var color = ColorOf(key);
        // Motion off in Settings: no halo at all.
        if (Motion)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(_clock.Elapsed.TotalSeconds * 2.4);
            var halo = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(70 + 50 * pulse), color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
            dc.DrawEllipse(halo, null, at, 15 + 3 * pulse, 15 + 3 * pulse);
        }
        dc.DrawEllipse(null, new Pen(brush, 1.6), at, 8, 8);
        dc.DrawEllipse(brush, new Pen(Res("Card"), 1.5), at, 4.5, 4.5);
        return at;
    }

    // ---- hover -----------------------------------------------------------------------------

    /// <summary>A vertical line with what every line says at that temperature.</summary>
    private void Crosshair(DrawingContext dc, Point m)
    {
        int c = (int)Math.Round(TempAt(m.X));
        dc.DrawLine(new Pen(Res("SubText"), 1, DashStyle.Dash), At(c, 0), At(c, 100));

        var lines = new List<(string, IBrush)> { ($"{c} °C", Res("Text")) };
        if (_profile is { IsEcAuto: false } profile)
        {
            int cpu = Math.Max(profile.CpuCurve.Evaluate(c), Lowest);
            if (SameCurve(profile))
                lines.Add(($"Profil: %{cpu}  (~{Texts.Rpm(cpu)} rpm)", Res("Accent")));
            else
            {
                int gpu = Math.Max(profile.GpuCurve.Evaluate(c), Lowest);
                lines.Add(($"İşlemci fanı: %{cpu}  (~{Texts.Rpm(cpu)} rpm)", Res("Accent")));
                lines.Add(($"Ekran kartı fanı: %{gpu}  (~{Texts.Rpm(gpu)} rpm)", Res("Ok")));
            }
        }
        if (ShowStock) lines.Add(($"{StockLabel}: %{StepAt(StockSteps, c)}", Res("SubText")));
        lines.Add(($"İndirilemeyen alt sınır: %{StepAt(Floors, c)}", Res("Bad")));
        Box(dc, lines, At(c, 100).X + 10, _plot.Top + 6, m.X);
    }

    /// <summary>Why this fan runs as it does right now.</summary>
    private void DotTip(DrawingContext dc, FanId fan, Point at)
    {
        var s = _status!;
        bool cpu = fan == FanId.Cpu;
        int? temp = cpu ? s.CpuTempC : s.GpuTempC, rpm = cpu ? s.CpuRpm : s.GpuRpm;
        int? curve = cpu ? s.CpuCurvePercent : s.GpuCurvePercent, now = cpu ? s.CpuAppliedPercent : s.GpuAppliedPercent;
        var reason = cpu ? s.CpuReason : s.GpuReason;

        var lines = new List<(string, IBrush)>
        {
            ($"{Texts.Chip(fan)} · {temp} °C · {rpm} rpm", Res(cpu ? "Value" : "Ok")),
            ($"Şu an: %{now}" + (curve is { } k && k != now ? $"   (profil %{k} istiyor)" : ""), Res("Text")),
            (Texts.Reason(reason, fan), Res("SubText")),
        };
        Box(dc, lines, at.X + 16, at.Y - 10, at.X);
    }

    private void Box(DrawingContext dc, List<(string Text, IBrush Brush)> lines, double x, double y, double anchorX)
    {
        var texts = lines.Select((l, i) => Text(l.Text, l.Brush, i == 0 ? 13 : 12, i == 0)).ToList();
        double w = texts.Max(t => t.Width) + 20, h = texts.Sum(t => t.Height + 2) + 14;
        // Flip to the left of the cursor near the right edge, and keep inside the plot.
        if (x + w > _plot.Right) x = anchorX - w - 16;
        x = Math.Max(_plot.Left, x);
        y = Math.Clamp(y, _plot.Top, Math.Max(_plot.Top, _plot.Bottom - h));
        dc.DrawRectangle(Res("Card"), new Pen(Res("Border"), 1), new RoundedRect(new Rect(x, y, w, h), 6));
        double ty = y + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(x + 10, ty));
            ty += t.Height + 2;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static bool Near(Point? dot, Point m) => dot is { } d && ((Vector)(d - m)).Length <= 12;

    private static bool SameCurve(FanProfile p) => p.CpuCurve.Points.SequenceEqual(p.GpuCurve.Points);

    /// <summary>The value of a step polyline (stock EC, floors) at a temperature: the last point at or below it.</summary>
    private static int StepAt((int C, int P)[] steps, int c) => steps.Last(s => s.C <= Math.Max(c, MinC)).P;

    // Through the curve's own corners (and where it crosses the lowest percent), not one rounded percent per degree.
    private void Curve(DrawingContext dc, FanCurve curve, Pen pen)
    {
        var corners = new List<(double C, double P)> { (MinC, curve.Evaluate(MinC)) };
        corners.AddRange(curve.Points.Where(p => p.TempC > MinC && p.TempC < MaxC).Select(p => ((double)p.TempC, (double)p.Percent)));
        corners.Add((MaxC, curve.Evaluate(MaxC)));
        var points = new List<Point>();
        int lowest = Lowest;
        for (int i = 0; i < corners.Count; i++)
        {
            if (i > 0 && (corners[i - 1].P < lowest) != (corners[i].P < lowest))
            {
                var (a, b) = (corners[i - 1], corners[i]);
                points.Add(At(a.C + (lowest - a.P) / (b.P - a.P) * (b.C - a.C), lowest));
            }
            points.Add(At(corners[i].C, Math.Max(corners[i].P, lowest)));
        }
        PolylinePoints(dc, points, pen);
    }

    private void Polyline(DrawingContext dc, (int C, int P)[] points, Pen pen) =>
        PolylinePoints(dc, points.Select(p => At(p.C, p.P)).ToList(), pen);

    private static void PolylinePoints(DrawingContext dc, List<Point> points, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            foreach (var p in points.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    private void Label(DrawingContext dc, string text, Point at, IBrush brush, bool center = false, bool right = false)
    {
        var ft = Text(text, brush, 11, false);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X, at.Y));
    }

    private FormattedText Text(string text, IBrush brush, double size, bool bold)
    {
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
    }
}
