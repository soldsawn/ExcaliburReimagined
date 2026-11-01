using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Excalibur.App;
using Excalibur.Core.Fan;
using Excalibur.Core.History;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>
/// Temperatures and fan speeds over time, the Windows window's HistoryChart in Avalonia: the upper pane the CPU and GPU
/// temperature (line = average of each bucket, faint band up to the bucket's maximum), the lower pane both fans' percent.
/// Gaps where nothing was recorded (sleep, service stopped) stay empty. Hovering shows the moment's values. (Windows also
/// marks its event log's EC errors and sudden shutdowns; Linux has no such log.)
/// </summary>
internal sealed class HistoryChart : Control
{
    private const int MinC = 30, MaxC = 100, WarmC = 65;
    private const double PaneGap = 26;
    // The app runs with invariant globalization: Turkish day names by hand.
    private static readonly string[] Days = ["Paz", "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt"];

    private HistorySeries? _series;
    private Point? _mouse;
    private Rect _temps, _fans;

    public HistoryChart()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        PointerMoved += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        PointerExited += (_, _) => { _mouse = null; InvalidateVisual(); };
    }

    public void Show(HistorySeries? series)
    {
        _series = series;
        InvalidateVisual();
    }

    private IBrush Res(string key) => Palette.Brush(this, key);

    private Color ColorOf(string key) => Res(key) is ISolidColorBrush b ? b.Color : Colors.Gray;

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        double height = Math.Max(0, Bounds.Height - 22 - PaneGap);
        _temps = new Rect(44, 8, Math.Max(0, Bounds.Width - 56), height * 0.62);
        _fans = new Rect(44, _temps.Bottom + PaneGap, _temps.Width, height * 0.38);
        if (_temps.Width < 60 || _fans.Height < 30 || _series is not { Points.Count: > 0 } s) return;

        Zones(dc);
        Grid(dc, s);
        Band(dc, s, p => p.GpuTempC, p => p.GpuTempMax, "Ok");
        Band(dc, s, p => p.CpuTempC, p => p.CpuTempMax, "Value");
        Line(dc, s, p => p.GpuTempC, TempY, "Ok");
        Line(dc, s, p => p.CpuTempC, TempY, "Value");
        Line(dc, s, p => p.GpuApplied, FanY, "Ok");
        Line(dc, s, p => p.CpuApplied, FanY, "Value");

        if (_mouse is { } m && m.X >= _temps.Left && m.X <= _temps.Right) Hover(dc, s, m.X);
    }

    // ---- geometry ---------------------------------------------------------------------------

    private double X(HistorySeries s, int i) => _temps.Left + (i + 0.5) / s.Points.Count * _temps.Width;

    private double TempY(double c) => _temps.Bottom - (Math.Clamp(c, MinC, MaxC) - MinC) / (MaxC - MinC) * _temps.Height;

    private double FanY(double p) => _fans.Bottom - Math.Clamp(p, 0, 100) / 100 * _fans.Height;

    /// <summary>Runs of consecutive recorded points: a gap ends a run, so the line is not joined across it.</summary>
    private static IEnumerable<List<(int I, HistoryPoint P)>> Runs(HistorySeries s)
    {
        var run = new List<(int, HistoryPoint)>();
        for (int i = 0; i < s.Points.Count; i++)
        {
            if (s.Points[i] is { } p) { run.Add((i, p)); continue; }
            if (run.Count > 0) { yield return run; run = []; }
        }
        if (run.Count > 0) yield return run;
    }

    private static string Format(DateTimeOffset t, bool withDay, bool seconds) =>
        (withDay ? Days[(int)t.DayOfWeek] + " " : "") + t.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);

    // ---- layers -----------------------------------------------------------------------------

    private void Zones(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(ColorOf("Warn"), 0.07), null, new Rect(new Point(_temps.Left, TempY(HotTracker.HotC)), new Point(_temps.Right, TempY(WarmC))));
        dc.DrawRectangle(new SolidColorBrush(ColorOf("Bad"), 0.10), null, new Rect(new Point(_temps.Left, TempY(MaxC)), new Point(_temps.Right, TempY(HotTracker.HotC))));
    }

    private void Grid(DrawingContext dc, HistorySeries s)
    {
        var grid = new Pen(Res("Border"), 1);
        for (int c = MinC; c <= MaxC; c += 10)
        {
            dc.DrawLine(grid, new Point(_temps.Left, TempY(c)), new Point(_temps.Right, TempY(c)));
            Label(dc, $"{c}°", new Point(_temps.Left - 8, TempY(c) - 8), right: true);
        }
        for (int p = 0; p <= 100; p += 50)
        {
            dc.DrawLine(grid, new Point(_fans.Left, FanY(p)), new Point(_fans.Right, FanY(p)));
            Label(dc, $"%{p}", new Point(_fans.Left - 8, FanY(p) - 8), right: true);
        }
        Label(dc, "Sıcaklık", new Point(_temps.Left + 6, _temps.Top + 2));
        Label(dc, "Fan", new Point(_fans.Left + 6, _fans.Top - 18));

        // About six time labels, on round local times.
        var from = s.From.ToLocalTime();
        var span = TimeSpan.FromSeconds((double)s.StepSeconds * s.Points.Count);
        var tick = new[] { 1, 2, 3, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440 }.Select(m => TimeSpan.FromMinutes(m)).FirstOrDefault(t => span / t <= 7, TimeSpan.FromDays(1));
        var first = new DateTimeOffset(from.Ticks / tick.Ticks * tick.Ticks, from.Offset) + tick;
        bool withDay = span > TimeSpan.FromDays(1);
        for (var t = first; t < from + span; t += tick)
        {
            double x = _temps.Left + (t - from).TotalSeconds / span.TotalSeconds * _temps.Width;
            dc.DrawLine(grid, new Point(x, _temps.Top), new Point(x, _fans.Bottom));
            Label(dc, Format(t, withDay, seconds: false), new Point(x, _fans.Bottom + 4), center: true);
        }
    }

    private void Band(DrawingContext dc, HistorySeries s, Func<HistoryPoint, int> avg, Func<HistoryPoint, int> max, string key)
    {
        var brush = new SolidColorBrush(ColorOf(key), 0.16);
        foreach (var run in Runs(s))
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(s, run[0].I), TempY(max(run[0].P))), true);
                foreach (var r in run.Skip(1)) ctx.LineTo(new Point(X(s, r.I), TempY(max(r.P))));
                foreach (var r in Enumerable.Reverse(run)) ctx.LineTo(new Point(X(s, r.I), TempY(avg(r.P))));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(brush, null, g);
        }
    }

    private void Line(DrawingContext dc, HistorySeries s, Func<HistoryPoint, int> value, Func<double, double> y, string key)
    {
        var pen = new Pen(Res(key), 1.8, lineJoin: PenLineJoin.Round);
        foreach (var run in Runs(s))
        {
            var pts = run.Select(r => new Point(X(s, r.I), y(value(r.P)))).ToList();
            if (pts.Count == 1) { dc.DrawEllipse(Res(key), null, pts[0], 1.5, 1.5); continue; }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], false);
                foreach (var p in pts.Skip(1)) ctx.LineTo(p);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, g);
        }
    }

    // ---- hover ------------------------------------------------------------------------------

    private void Hover(DrawingContext dc, HistorySeries s, double x)
    {
        int i = Math.Clamp((int)((x - _temps.Left) / _temps.Width * s.Points.Count), 0, s.Points.Count - 1);
        double at = X(s, i);
        dc.DrawLine(new Pen(Res("SubText"), 1, DashStyle.Dash), new Point(at, _temps.Top), new Point(at, _fans.Bottom));

        var time = s.From.ToLocalTime().AddSeconds((double)s.StepSeconds * i);
        bool long_ = s.StepSeconds * s.Points.Count > 86400;
        var lines = new List<(string, IBrush)> { (Format(time, long_, seconds: !long_), Res("Text")) };
        if (s.Points[i] is { } p)
        {
            dc.DrawEllipse(Res("Value"), null, new Point(at, TempY(p.CpuTempC)), 3.5, 3.5);
            dc.DrawEllipse(Res("Ok"), null, new Point(at, TempY(p.GpuTempC)), 3.5, 3.5);
            lines.Add(($"{Texts.Chip(FanId.Cpu)}: {p.CpuTempC} °C" + (p.CpuTempMax > p.CpuTempC ? $" (en yüksek {p.CpuTempMax})" : "") + $" · fan %{p.CpuApplied}", Res("Value")));
            lines.Add(($"{Texts.Chip(FanId.Gpu)}: {p.GpuTempC} °C" + (p.GpuTempMax > p.GpuTempC ? $" (en yüksek {p.GpuTempMax})" : "") + $" · fan %{p.GpuApplied}", Res("Ok")));
        }
        else lines.Add(("Kayıt yok (uyku ya da servis kapalı)", Res("SubText")));
        Box(dc, lines, at + 12, _temps.Top + 6, at);
    }

    private void Box(DrawingContext dc, List<(string Text, IBrush Brush)> lines, double x, double y, double anchorX)
    {
        var texts = lines.Select((l, i) => Text(l.Text, l.Brush, i == 0 ? 13 : 12, i == 0)).ToList();
        double w = texts.Max(t => t.Width) + 20, h = texts.Sum(t => t.Height + 2) + 14;
        if (x + w > _temps.Right) x = anchorX - w - 12;
        x = Math.Max(_temps.Left, x);
        dc.DrawRectangle(Res("Card"), new Pen(Res("Border"), 1), new RoundedRect(new Rect(x, y, w, h), 6));
        double ty = y + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(x + 10, ty));
            ty += t.Height + 2;
        }
    }

    private void Label(DrawingContext dc, string text, Point at, bool center = false, bool right = false)
    {
        var ft = Text(text, Res("SubText"), 11, false);
        dc.DrawText(ft, new Point(right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X, at.Y));
    }

    private FormattedText Text(string text, IBrush brush, double size, bool bold)
    {
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
    }
}
