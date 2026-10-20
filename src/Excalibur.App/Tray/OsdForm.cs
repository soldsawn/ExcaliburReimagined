using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Excalibur.App.Tray;

/// <summary>
/// A small notice above the taskbar ("Dengeli · Fan profili") that never takes focus, lets
/// clicks through, and fades out by itself.
/// </summary>
internal sealed class OsdForm : Form
{
    private const int ShowMs = 1600, FadeMs = 300, StepMs = 30;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = StepMs };
    private string _title = "", _subtitle = "";
    private Color _dot = Theme.Ok;
    private Theme _theme = Theme.Current;
    private int _elapsed;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Flash(string title, string subtitle, Color dot)
    {
        (_title, _subtitle, _dot) = (title, subtitle, dot);
        _theme = Theme.Current;
        BackColor = _theme.Back;
        float k = DeviceDpi / 96f;
        Size = new Size((int)(240 * k), (int)(64 * k));
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - (int)(56 * k));

        _elapsed = 0;
        Opacity = 0.96;
        if (!Visible) Show();
        Native.Round(Handle, _theme.IsDark);
        Invalidate();
        _timer.Start();
    }

    /// <summary>Paints the notice into a bitmap (development snapshots).</summary>
    public Bitmap Snapshot()
    {
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }

    private void Step()
    {
        _elapsed += StepMs;
        if (_elapsed < ShowMs) return;
        double left = 1 - (_elapsed - ShowMs) / (double)FadeMs;
        if (left > 0) { Opacity = 0.96 * left; return; }
        _timer.Stop();
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float k = DeviceDpi / 96f;
        using (var border = new Pen(_theme.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        float d = 10 * k;
        using (var dot = new SolidBrush(_dot))
            g.FillEllipse(dot, 20 * k, (Height - d) / 2, d, d);

        using var title = new Font("Segoe UI Semibold", 15 * k, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 12 * k, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, _title, title, new Point((int)(42 * k), (int)(12 * k)), _theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _subtitle, sub, new Point((int)(42 * k), (int)(34 * k)), _theme.SubText, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
