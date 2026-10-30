using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Excalibur.Core.Fan;
using Excalibur.Core.Fan.Ipc;

namespace Excalibur.App.Tray;

/// <summary>
/// The left-click panel, in the style of the Windows 11 quick settings flyout: CPU/GPU tiles,
/// profile buttons, a status line, a link to the main window and (only when faulted) "Tekrar dene".
/// Everything is painted here so it follows the light/dark theme; it hides when it loses focus.
/// Layout is in 96-dpi units, scaled by <see cref="Control.DeviceDpi"/>.
/// </summary>
internal sealed class TrayPanel : Form
{
    private const int W = 340, H = 316, Pad = 16;

    public event Action<string>? ProfileClicked;
    public event Action? DetailsClicked;
    public event Action? ClearFaultClicked;

    private FanStatusDto? _status;
    private bool _connected;
    private Theme _theme = Theme.Current;
    private string? _hover;
    private string? _pending; // profile clicked, waiting for the service to confirm
    private IReadOnlyList<(string Name, string Short)> _slots = ProfileNames.All.Select(p => (p.Name, p.Short)).ToList();

    /// <summary>When the panel last hid itself, so the tray click that caused it does not reopen it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DateTime HiddenAt { get; private set; }

    public TrayPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = TrayContext.AppName;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private float K => DeviceDpi / 96f;
    private Rectangle S(float x, float y, float w, float h) => new((int)(x * K), (int)(y * K), (int)(w * K), (int)(h * K));

    /// <summary>Opens the panel in the corner of the screen nearest to the tray icon (the cursor).</summary>
    public void Popup()
    {
        _theme = Theme.Current;
        BackColor = _theme.Back;
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        Size = S(0, 0, W, H).Size;
        int gap = (int)(12 * K);
        int x = cursor.X > area.Left + area.Width / 2 ? area.Right - Width - gap : area.Left + gap;
        int y = cursor.Y > area.Top + area.Height / 2 ? area.Bottom - Height - gap : area.Top + gap;
        Location = new Point(x, y);
        Show();
        Native.Round(Handle, _theme.IsDark);
        Activate();
        Invalidate();
    }

    public void ShowStatus(FanStatusDto? status, bool connected)
    {
        _status = status;
        _connected = connected;
        if (_pending is not null && status?.Profile == _pending) _pending = null;
        if (Visible) Invalidate();
    }

    /// <summary>The profile buttons (1-4), from the user's quick-access slots.</summary>
    public void SetSlots(IReadOnlyList<(string Name, string Short)> slots)
    {
        _slots = slots;
        if (Visible) Invalidate();
    }

    public void CommandFailed()
    {
        _pending = null;
        if (Visible) Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HiddenAt = DateTime.UtcNow;
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    // ---- layout ----------------------------------------------------------------------------

    private IEnumerable<(string Id, Rectangle Rect)> HitAreas()
    {
        int n = Math.Max(1, _slots.Count);
        float bw = (W - 2 * Pad - (n - 1) * 6) / n;
        for (int i = 0; i < _slots.Count; i++)
            yield return ("p:" + _slots[i].Name, S(Pad + i * (bw + 6), 202, bw, 38));
        yield return ("details", S(Pad, H - 34, 200, 22));
        if (_status?.Mode == FanLoopMode.Faulted)
            yield return ("clear", S(W - Pad - 130, H - 36, 130, 26));
    }

    private string? HitTest(Point p) => HitAreas().FirstOrDefault(a => a.Rect.Contains(p)).Id;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        if (hit != _hover) { _hover = hit; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover is not null) { _hover = null; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case { } id when id.StartsWith("p:"):
                if (!_connected) return;
                _pending = id[2..];
                Invalidate();
                ProfileClicked?.Invoke(_pending);
                break;
            case "details":
                Hide();
                DetailsClicked?.Invoke();
                break;
            case "clear":
                ClearFaultClicked?.Invoke();
                break;
        }
    }

    // ---- painting --------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var t = _theme;
        var s = _status;

        using var brand = MakeFont(22, weight: "Semibold");
        using var tag = MakeFont(11, weight: "Semibold");
        using var small = MakeFont(12);
        // Big readings in the narrow DIN face, as on the tray icon and in the main window.
        using var big = new Font("Bahnschrift SemiBold", 32 * K, FontStyle.Regular, GraphicsUnit.Pixel);
        using var label = MakeFont(12, weight: "Semibold");
        using var mono = new Font("Consolas", 12 * K, GraphicsUnit.Pixel);

        using (var border = new Pen(t.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        // header: thin brand name like SawnTask, but in the accent with a "REIMAGINED" tag; state on the right
        DrawLabel(g, "Excalibur", brand, Pad - 1, 10, t.Accent);
        int brandW = TextRenderer.MeasureText(g, "Excalibur", brand, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, "REIMAGINED", tag, new Point((int)((Pad + 6) * K) + brandW, (int)(20 * K)), t.SubText, TextFormatFlags.NoPadding);
        var (state, color) = State(s);
        var stateSize = TextRenderer.MeasureText(g, state, small, Size.Empty, TextFormatFlags.NoPadding);
        int stateX = Width - (int)(Pad * K) - stateSize.Width;
        TextRenderer.DrawText(g, state, small, new Point(stateX, (int)(19 * K)), t.SubText, TextFormatFlags.NoPadding);
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, stateX - 14 * K, 22 * K, 8 * K, 8 * K);

        // the gaming touch: an accent line under the header that fades out to the right
        var line = S(Pad, 46, W - 2 * Pad, 2);
        using (var grad = new LinearGradientBrush(line, t.Accent, Color.FromArgb(0, t.Accent), LinearGradientMode.Horizontal))
            g.FillRectangle(grad, line);

        // CPU / GPU tiles
        float tw = (W - 2 * Pad - 8) / 2f;
        Tile(g, S(Pad, 58, tw, 112), "CPU", s?.CpuTempC, s?.CpuRpm, s?.CpuAppliedPercent, s?.CpuTarget, big, mono, label);
        Tile(g, S(Pad + tw + 8, 58, tw, 112), "GPU", s?.GpuTempC, s?.GpuRpm, s?.GpuAppliedPercent, s?.GpuTarget, big, mono, label);

        // profiles: the selected one is outlined and tinted in the accent (not filled solid)
        DrawLabel(g, "Fan profili", label, Pad, 182, t.SubText);
        string selected = _pending ?? s?.Profile ?? "";
        foreach (var (id, rect) in HitAreas().Where(a => a.Id.StartsWith("p:")))
        {
            string name = id[2..];
            bool on = name == selected && _connected;
            var back = on ? Blend(t.Card, t.Accent, 0.22) : _hover == id && _connected ? t.CardHover : t.Card;
            using (var path = TrayIconRenderer.RoundedRect(rect, 6 * K))
            {
                using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
                using var pen = new Pen(on ? t.Accent : t.Border, on ? 1.5f * K : 1);
                g.DrawPath(pen, path);
            }
            if (on)
            {
                var bar = new RectangleF(rect.X + rect.Width * 0.3f, rect.Bottom - 4 * K, rect.Width * 0.4f, 2 * K);
                using var glow = new SolidBrush(t.Accent);
                g.FillRectangle(glow, bar);
            }
            string text = _slots.First(p => p.Name == name).Short;
            TextRenderer.DrawText(g, text, small, rect, on ? t.Text : _connected ? t.Text : t.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // status line
        DrawLabel(g, Info(s), small, Pad, 252, t.SubText);

        // footer
        using (var link = MakeFont(12, underline: _hover == "details"))
            DrawLabel(g, TrayContext.OpenLabel + "  →", link, Pad, H - 30, t.Value);
        if (s?.Mode == FanLoopMode.Faulted)
        {
            var rect = S(W - Pad - 130, H - 36, 130, 26);
            using var path = TrayIconRenderer.RoundedRect(rect, 6 * K);
            using (var fill = new SolidBrush(_hover == "clear" ? Color.FromArgb(200, 50, 62) : Theme.Bad)) g.FillPath(fill, path);
            TextRenderer.DrawText(g, "Tekrar dene", small, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void Tile(Graphics g, Rectangle r, string name, int? tempC, int? rpm, int? applied, int? target,
        Font big, Font mono, Font label)
    {
        var t = _theme;
        using (var path = TrayIconRenderer.RoundedRect(r, 8 * K))
        {
            using (var fill = new SolidBrush(t.Card)) g.FillPath(fill, path);
            using (var pen = new Pen(t.Border)) g.DrawPath(pen, path);
        }
        int x = r.X + (int)(12 * K);
        TextRenderer.DrawText(g, name, label, new Point(x, r.Y + (int)(10 * K)), t.SubText, TextFormatFlags.NoPadding);
        string temp = _connected && tempC is { } c ? $"{c}°C" : "–";
        TextRenderer.DrawText(g, temp, big, new Point(x - (int)(2 * K), r.Y + (int)(26 * K)),
            _connected ? Theme.Temperature(tempC, t.Value) : t.SubText, TextFormatFlags.NoPadding);

        // Under the EC's own control there is no target, but the speed worked out from the RPM still fills the bar
        // (the panel names the profile, so the narrow tile shows only the percent).
        int? percent = applied ?? target;
        string fan = !_connected ? "bağlantı yok" : $"{rpm?.ToString() ?? "–",4} rpm " + (percent is { } p ? $"%{p}" : "orijinal");
        TextRenderer.DrawText(g, fan, mono, new Point(x, r.Bottom - (int)(36 * K)), t.SubText, TextFormatFlags.NoPadding);

        // fan duty as a block bar
        const int blocks = 14;
        float gap = 2 * K, bw = (r.Width - 24 * K - (blocks - 1) * gap) / blocks, by = r.Bottom - 16 * K, bh = 6 * K;
        int lit = _connected && percent is { } q ? (int)Math.Round(q / 100.0 * blocks) : 0;
        using var on = new SolidBrush(_connected && percent is not null ? Theme.Temperature(tempC, t.Value) : t.Border);
        using var off = new SolidBrush(t.CardHover);
        for (int i = 0; i < blocks; i++)
            g.FillRectangle(i < lit ? on : off, x + i * (bw + gap), by, bw, bh);
    }

    private static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

    private (string, Color) State(FanStatusDto? s)
    {
        if (!_connected || s is null) return ("Bağlantı yok", Theme.Off);
        string sim = s.Simulated ? " (deneme)" : "";
        return s.Mode switch
        {
            FanLoopMode.Running => ("Çalışıyor" + sim, Theme.Ok),
            FanLoopMode.MonitorOnly => ("Yalnız izleniyor" + sim, Theme.Warn),
            FanLoopMode.Faulted => ("Sorun var" + sim, Theme.Bad),
            _ => ("Durdu" + sim, Theme.Off),
        };
    }

    private string Info(FanStatusDto? s)
    {
        if (!_connected || s is null) return "Fan kontrol hizmeti yanıt vermiyor.";
        var parts = new List<string> { ProfileNames.Label(s.Profile, s.ManualUntil) };
        if (s.CoolingBoost) parts.Add("Fn+1 turbo açık");
        parts.Add(s.HoldsFans ? "fanlar bu uygulamada" : "fanlar orijinal ayarda");
        if (s.ConsecutiveReadFailures > 0) parts.Add("okuma sorunu");
        return string.Join(" · ", parts);
    }

    private void DrawLabel(Graphics g, string text, Font font, float x, float y, Color color) =>
        TextRenderer.DrawText(g, text, font, new Point((int)(x * K), (int)(y * K)), color, TextFormatFlags.NoPadding);

    private Font MakeFont(float px, string? weight = null, bool underline = false) =>
        new(weight is null ? "Segoe UI" : "Segoe UI " + weight, px * K, underline ? FontStyle.Underline : FontStyle.Regular, GraphicsUnit.Pixel);
}
