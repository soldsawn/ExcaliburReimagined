using System.Drawing.Drawing2D;
using Excalibur.Core.Fan;

namespace Excalibur.FanTestUi;

/// <summary>
/// Draws the selected profile's curve (temperature -> fan %), the safety floors, the measured
/// stock EC curve for reference, and where the CPU and GPU fans are right now.
/// </summary>
internal sealed class CurveView : Control
{
    private const int MinC = 30, MaxC = 100;
    // Measured stock EC CPU-fan steps (docs/07 A.2): <58 C 66%, 58-64 C 86%, >=65 C ~94%.
    private static readonly (int C, int P)[] Stock = [(MinC, 66), (58, 66), (58, 86), (65, 86), (65, 94), (MaxC, 94)];

    private FanProfile? _profile;
    private (int C, int P)? _cpu, _gpu;

    public CurveView()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        MinimumSize = new Size(320, 200);
    }

    public void Show(FanProfile? profile, (int C, int P)? cpu, (int C, int P)? gpu)
    {
        _profile = profile;
        _cpu = cpu;
        _gpu = gpu;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SystemColors.Window);
        var plot = new Rectangle(40, 10, Width - 55, Height - 35);
        if (plot.Width < 50 || plot.Height < 50) return;

        PointF At(double c, double p) => new(
            plot.Left + (float)((c - MinC) / (MaxC - MinC) * plot.Width),
            plot.Bottom - (float)(p / 100 * plot.Height));

        using var grid = new Pen(Color.Gainsboro);
        using var font = new Font(Font.FontFamily, 8);
        for (int c = MinC; c <= MaxC; c += 10)
        {
            g.DrawLine(grid, At(c, 0), At(c, 100));
            g.DrawString($"{c}°", font, Brushes.Gray, At(c, 0).X - 8, plot.Bottom + 3);
        }
        for (int p = 0; p <= 100; p += 25)
        {
            g.DrawLine(grid, At(MinC, p), At(MaxC, p));
            g.DrawString($"%{p}", font, Brushes.Gray, 2, At(MinC, p).Y - 7);
        }

        using (var stock = new Pen(Color.DarkGray, 1.5f) { DashStyle = DashStyle.Dash })
            g.DrawLines(stock, Stock.Select(s => At(s.C, s.P)).ToArray());

        // Safety floors: 25% always, 80% from 65 C, 90% from 72 C, 100% when hot (>= 80 C for 3 s).
        using (var floor = new Pen(Color.IndianRed, 1f) { DashStyle = DashStyle.Dot })
        {
            g.DrawLines(floor,
            [
                At(MinC, SafetyPolicy.AbsoluteFloor), At(65, SafetyPolicy.AbsoluteFloor), At(65, 80), At(72, 80),
                At(72, 90), At(HotTracker.HotC, 90), At(HotTracker.HotC, 100), At(MaxC, 100),
            ]);
        }

        if (_profile is { IsEcAuto: false } profile)
        {
            using var curve = new Pen(Color.RoyalBlue, 2.5f);
            var pts = Enumerable.Range(MinC, MaxC - MinC + 1)
                .Select(c => At(c, Math.Max(profile.CpuCurve.Evaluate(c), SafetyPolicy.AbsoluteFloor))).ToArray();
            g.DrawLines(curve, pts);
        }

        Dot(g, _cpu, Color.OrangeRed, "CPU", At);
        Dot(g, _gpu, Color.SeaGreen, "GPU", At);

        g.DrawString("mavi: profil   gri kesik: stok EC   kırmızı nokta: güvenlik tabanı", font, Brushes.Gray, plot.Left, plot.Top);
    }

    private void Dot(Graphics g, (int C, int P)? v, Color color, string label, Func<double, double, PointF> at)
    {
        if (v is not { } d) return;
        var p = at(Math.Clamp(d.C, MinC, MaxC), d.P);
        using var b = new SolidBrush(color);
        g.FillEllipse(b, p.X - 5, p.Y - 5, 10, 10);
        using var font = new Font(Font.FontFamily, 8, FontStyle.Bold);
        g.DrawString(label, font, b, p.X + 6, p.Y - 14);
    }
}
