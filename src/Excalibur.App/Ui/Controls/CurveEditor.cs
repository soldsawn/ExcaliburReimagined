using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Excalibur.Core.Fan;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// Draws one fan curve (temperature -> fan %) with points the user drags, in the way FanControl's graph
/// curve works: drag a point, click empty space to add one, right-click a point (or press Delete) to remove it.
/// The editor only ever produces curves the service accepts (<see cref="CustomProfile.Validate"/>): temperatures
/// strictly increase, percent never drops as it gets hotter, nothing goes below the 25% floor, 2 to 12 points.
/// The safety floors the service always applies on top are drawn so the user sees what cannot be undercut.
/// </summary>
internal sealed class CurveEditor : FrameworkElement
{
    private const int MinC = 30, MaxC = 100, MinPoints = 2;
    private const double HitRadius = 11;

    private List<CurvePoint> _points = [];
    private Rect _plot;
    private int? _hover, _drag;
    private Point _mouse;
    private bool _editable = true;

    public CurveEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        MouseMove += OnMove;
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += (_, _) => { if (_drag is not null) { _drag = null; ReleaseMouseCapture(); Changed?.Invoke(); } };
        MouseRightButtonDown += OnRightDown;
        KeyDown += OnKey;
    }

    /// <summary>Raised after the user finished a change (a drop, an added or removed point).</summary>
    public event Action? Changed;

    /// <summary>The curve's points, sorted by temperature.</summary>
    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>A vertical marker for a current temperature (the live reading), or null.</summary>
    public int? LiveTempC { get; set; }

    public string? LiveLabel { get; set; }

    public void Load(IEnumerable<CurvePoint> points, bool editable)
    {
        _points = points.OrderBy(p => p.TempC).ToList();
        _editable = editable;
        _hover = _drag = null;
        Cursor = editable ? Cursors.Cross : Cursors.Arrow;
        InvalidateVisual();
    }

    // ---- geometry ----------------------------------------------------------------------------

    private Point At(double c, double p) => new(
        _plot.Left + (c - MinC) / (MaxC - MinC) * _plot.Width,
        _plot.Bottom - p / 100 * _plot.Height);

    private double TempAt(double x) => MinC + (x - _plot.Left) / _plot.Width * (MaxC - MinC);
    private double PercentAt(double y) => (_plot.Bottom - y) / _plot.Height * 100;

    private int? PointAt(Point m)
    {
        int? best = null;
        double bestD = HitRadius;
        for (int i = 0; i < _points.Count; i++)
        {
            double d = (At(_points[i].TempC, _points[i].Percent) - m).Length;
            if (d <= bestD) { best = i; bestD = d; }
        }
        return best;
    }

    // ---- mouse and keys ----------------------------------------------------------------------

    private void OnMove(object? sender, MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_drag is { } i && _editable)
        {
            _points[i] = Clamp(i, TempAt(_mouse.X), PercentAt(_mouse.Y));
            InvalidateVisual();
            return;
        }
        int? hover = PointAt(_mouse);
        if (hover != _hover) _hover = hover;
        InvalidateVisual();
    }

    private void OnDown(object? sender, MouseButtonEventArgs e)
    {
        Focus();
        if (!_editable || !_plot.Contains(e.GetPosition(this))) return;
        _mouse = e.GetPosition(this);
        if (PointAt(_mouse) is { } hit)
        {
            _drag = hit;
            CaptureMouse();
            return;
        }
        if (_points.Count >= CustomProfile.MaxPoints) return;
        // A new point where the user clicked, kept between its neighbours so the curve stays valid.
        double t = Math.Round(TempAt(_mouse.X));
        if (_points.Any(p => p.TempC == (int)t)) return;
        int index = _points.FindIndex(p => p.TempC > t);
        if (index < 0) index = _points.Count;
        _points.Insert(index, new CurvePoint((int)t, SafetyPolicy.AbsoluteFloor));
        _points[index] = Clamp(index, t, PercentAt(_mouse.Y));
        _drag = index;
        CaptureMouse();
        InvalidateVisual();
    }

    private void OnRightDown(object? sender, MouseButtonEventArgs e)
    {
        if (!_editable) return;
        if (PointAt(e.GetPosition(this)) is { } hit) Remove(hit);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _editable && _hover is { } h) Remove(h);
    }

    private void Remove(int index)
    {
        if (_points.Count <= MinPoints) return;
        _points.RemoveAt(index);
        _hover = null;
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>The point as the mouse wants it, held inside what keeps the curve valid.</summary>
    private CurvePoint Clamp(int index, double tempC, double percent)
    {
        int minT = index > 0 ? _points[index - 1].TempC + 1 : MinC;
        int maxT = index < _points.Count - 1 ? _points[index + 1].TempC - 1 : MaxC;
        int minP = index > 0 ? _points[index - 1].Percent : SafetyPolicy.AbsoluteFloor;
        int maxP = index < _points.Count - 1 ? _points[index + 1].Percent : 100;
        if (maxT < minT) maxT = minT;
        return new CurvePoint(
            (int)Math.Clamp(Math.Round(tempC), minT, maxT),
            (int)Math.Clamp(Math.Round(percent), Math.Max(minP, SafetyPolicy.AbsoluteFloor), maxP));
    }

    // ---- drawing -----------------------------------------------------------------------------

    private Brush Res(string key) => (Brush)FindResource(key);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _plot = new Rect(40, 10, Math.Max(0, ActualWidth - 56), Math.Max(0, ActualHeight - 34));
        if (_plot.Width < 80 || _plot.Height < 80) return;

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

        // what the service never goes below, whatever the curve says
        Polyline(dc, CurveChart.Floors.Select(f => At(f.C, f.P)).ToList(), new Pen(Res("Bad"), 1.2) { DashStyle = DashStyles.Dot });

        if (_points.Count == 0) return;
        var line = new List<Point> { At(MinC, _points[0].Percent) };
        line.AddRange(_points.Select(p => At(p.TempC, p.Percent)));
        line.Add(At(MaxC, _points[^1].Percent));
        // the curve is flat beyond its first and last point: draw that part fainter
        var accent = Res("Accent");
        var fill = accent.CloneCurrentValue();
        fill.Opacity = 0.10;
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(line[0].X, _plot.Bottom), true, true);
            foreach (var pt in line) g.LineTo(pt, false, false);
            g.LineTo(new Point(line[^1].X, _plot.Bottom), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        Polyline(dc, line, new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round });

        if (LiveTempC is { } live && live >= MinC && live <= MaxC)
        {
            dc.DrawLine(new Pen(Res("Value"), 1) { DashStyle = DashStyles.Dash }, At(live, 0), At(live, 100));
            Label(dc, LiveLabel ?? $"şu an {live}°", At(live, 100) + new Vector(4, 2), Res("Value"));
        }

        for (int i = 0; i < _points.Count; i++)
        {
            var at = At(_points[i].TempC, _points[i].Percent);
            bool active = _hover == i || _drag == i;
            if (_editable)
            {
                if (active) dc.DrawEllipse(null, new Pen(accent, 1.5), at, 11, 11);
                dc.DrawEllipse(Res("Card"), new Pen(accent, 2.2), at, active ? 6.5 : 5.5, active ? 6.5 : 5.5);
                dc.DrawEllipse(accent, null, at, 2.5, 2.5);
            }
            else dc.DrawEllipse(accent, null, at, 3.5, 3.5);
        }

        int? current = _drag ?? _hover;
        if (current is { } shown)
        {
            var p = _points[shown];
            Tip(dc, $"{p.TempC} °C  →  %{p.Percent}", $"~{Texts.Rpm(p.Percent)} rpm", At(p.TempC, p.Percent));
        }
        else if (_editable && _plot.Contains(_mouse) && _points.Count < CustomProfile.MaxPoints)
        {
            // where a click would add a point
            double t = Math.Round(TempAt(_mouse.X));
            dc.DrawEllipse(null, new Pen(Res("SubText"), 1) { DashStyle = DashStyles.Dot }, At(t, Math.Clamp(PercentAt(_mouse.Y), 0, 100)), 6, 6);
        }
    }

    private void Polyline(DrawingContext dc, IReadOnlyList<Point> points, Pen pen)
    {
        if (points.Count < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private void Tip(DrawingContext dc, string title, string detail, Point anchor)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var a = Text(title, Res("Text"), 13, true, dpi);
        var b = Text(detail, Res("SubText"), 12, false, dpi);
        double w = Math.Max(a.Width, b.Width) + 20, h = a.Height + b.Height + 14;
        double x = anchor.X + 16, y = anchor.Y - h - 10;
        if (x + w > _plot.Right) x = anchor.X - w - 16;
        if (y < _plot.Top) y = anchor.Y + 14;
        dc.DrawRoundedRectangle(Res("Card"), new Pen(Res("Border"), 1), new Rect(x, y, w, h), 6, 6);
        dc.DrawText(a, new Point(x + 10, y + 6));
        dc.DrawText(b, new Point(x + 10, y + 8 + a.Height));
    }

    private void Label(DrawingContext dc, string text, Point at, Brush brush, bool center = false, bool right = false)
    {
        var ft = Text(text, brush, 11, false, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X, at.Y));
    }

    private static FormattedText Text(string text, Brush brush, double size, bool bold, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
}
