using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Excalibur.Core.Fan;

namespace Excalibur.Linux.Ui;

/// <summary>
/// The same dark palette and UI building blocks as the Windows application (<c>src/Excalibur.App/Ui/Palette.cs</c>).
/// </summary>
internal static class LinuxTheme
{
    public static readonly Color BgColor = Color.FromRgb(27, 27, 30);
    public static readonly Color SidebarColor = Color.FromRgb(22, 22, 25);
    public static readonly Color CardColor = Color.FromRgb(37, 37, 41);
    public static readonly Color CardHoverColor = Color.FromRgb(48, 48, 54);
    public static readonly Color BorderColor = Color.FromRgb(54, 54, 60);
    public static readonly Color TextColor = Color.FromRgb(237, 237, 237);
    public static readonly Color SubTextColor = Color.FromRgb(154, 154, 162);
    public static readonly Color ValueColor = Color.FromRgb(79, 195, 247);
    public static readonly Color AccentColor = Color.FromRgb(232, 72, 85);
    public static readonly Color AccentSoftColor = Color.FromRgb(74, 33, 40);
    public static readonly Color OkColor = Color.FromRgb(108, 195, 108);
    public static readonly Color WarnColor = Color.FromRgb(240, 160, 48);
    public static readonly Color BadColor = Color.FromRgb(232, 72, 85);
    public static readonly Color OffColor = Color.FromRgb(138, 138, 138);

    public static readonly IBrush Bg = new SolidColorBrush(BgColor);
    public static readonly IBrush Sidebar = new SolidColorBrush(SidebarColor);
    public static readonly IBrush Card = new SolidColorBrush(CardColor);
    public static readonly IBrush CardHover = new SolidColorBrush(CardHoverColor);
    public static readonly IBrush Border = new SolidColorBrush(BorderColor);
    public static readonly IBrush Text = new SolidColorBrush(TextColor);
    public static readonly IBrush SubText = new SolidColorBrush(SubTextColor);
    public static readonly IBrush Value = new SolidColorBrush(ValueColor);
    public static readonly IBrush Accent = new SolidColorBrush(AccentColor);
    public static readonly IBrush AccentSoft = new SolidColorBrush(AccentSoftColor);
    public static readonly IBrush Ok = new SolidColorBrush(OkColor);
    public static readonly IBrush Warn = new SolidColorBrush(WarnColor);
    public static readonly IBrush Bad = new SolidColorBrush(BadColor);
    public static readonly IBrush Off = new SolidColorBrush(OffColor);

    public static IBrush TempBrush(int? tempC) =>
        tempC >= HotTracker.HotC ? Bad : tempC >= 65 ? Warn : Value;

    public static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return Color.FromRgb(r, g, b);
        return fallback;
    }

    public static Border MakeCard(string? title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrEmpty(title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = Text,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }
        foreach (var child in children) stack.Children.Add(child);
        return new Border
        {
            Background = Card,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18, 16),
            Margin = new Thickness(0, 0, 0, 14),
            Child = stack,
        };
    }

    public static Button MakeChoiceButton(string label, bool active, Action onClick, string? subtitle = null)
    {
        Control content = subtitle is null
            ? new TextBlock
            {
                Text = label,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = active ? Brushes.White : Text,
                HorizontalAlignment = HorizontalAlignment.Center,
            }
            : new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = active ? Brushes.White : Text },
                    new TextBlock { Text = subtitle, FontSize = 11, Foreground = active ? Text : SubText },
                },
            };

        var btn = new Button
        {
            Content = content,
            Background = active ? AccentSoft : CardHover,
            BorderBrush = active ? Accent : Border,
            BorderThickness = new Thickness(active ? 1.5 : 1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Button MakeActionButton(string label, Action onClick, bool primary = false)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontWeight = FontWeight.SemiBold,
                Foreground = primary ? Brushes.White : Text,
            },
            Background = primary ? Accent : CardHover,
            BorderBrush = primary ? Accent : Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Control SectionTitle(string title, string? subtitle = null)
    {
        if (subtitle is null)
        {
            return new TextBlock
            {
                Text = title,
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                Foreground = Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            };
        }
        return new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Text },
                new TextBlock { Text = subtitle, FontSize = 13, Foreground = SubText, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = SubText,
        TextWrapping = TextWrapping.Wrap,
    };
}

/// <summary>
/// Draws the fan curve (35–95 °C vs 0–100 %), the hardware safety floor (25 % min, 70 °C -> 80 %, 78 °C -> 90 %, 80 °C -> 100 %)
/// and the live CPU/GPU operating points.
/// </summary>
internal sealed class FanCurveCanvas : Control
{
    public IReadOnlyList<CurvePoint>? Points { get; set; }
    public int? CpuTempC { get; set; }
    public int? CpuPercent { get; set; }
    public int? GpuTempC { get; set; }
    public int? GpuPercent { get; set; }

    public FanCurveCanvas()
    {
        Height = 220;
        MinWidth = 420;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 80) return;

        var bgRect = new Rect(0, 0, w, h);
        ctx.FillRectangle(LinuxTheme.Sidebar, bgRect, 6);
        ctx.DrawRectangle(new Pen(LinuxTheme.Border, 1), bgRect, 6);

        double padL = 42, padR = 18, padT = 16, padB = 26;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        const double minT = 35, maxT = 95;

        double X(double temp) => padL + Math.Clamp((temp - minT) / (maxT - minT), 0, 1) * plotW;
        double Y(double pct) => padT + (1.0 - Math.Clamp(pct / 100.0, 0, 1)) * plotH;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 80, 80, 90)), 1);
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            double y = Y(pct);
            ctx.DrawLine(gridPen, new Point(padL, y), new Point(w - padR, y));
            var ft = new FormattedText($"%{pct}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, LinuxTheme.SubText);
            ctx.DrawText(ft, new Point(6, y - ft.Height / 2));
        }

        foreach (int temp in new[] { 40, 50, 60, 70, 80, 90 })
        {
            double x = X(temp);
            ctx.DrawLine(gridPen, new Point(x, padT), new Point(x, h - padB));
            var ft = new FormattedText($"{temp}°C", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, LinuxTheme.SubText);
            ctx.DrawText(ft, new Point(x - ft.Width / 2, h - padB + 5));
        }

        // Safety floor dashed line (25% baseline, 70C->80%, 78C->90%, 80C->100%)
        var floorPts = new[]
        {
            new Point(X(minT), Y(25)),
            new Point(X(70), Y(25)),
            new Point(X(70), Y(80)),
            new Point(X(78), Y(80)),
            new Point(X(78), Y(90)),
            new Point(X(80), Y(90)),
            new Point(X(80), Y(100)),
            new Point(X(maxT), Y(100)),
        };
        var floorPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 240, 160, 48)), 1.5, DashStyle.Dash);
        for (int i = 0; i < floorPts.Length - 1; i++)
            ctx.DrawLine(floorPen, floorPts[i], floorPts[i + 1]);

        // Active curve
        if (Points is { Count: > 0 } pts)
        {
            var ordered = pts.OrderBy(p => p.TempC).ToList();
            var poly = new List<Point> { new(X(minT), Y(ordered[0].Percent)) };
            poly.AddRange(ordered.Select(p => new Point(X(p.TempC), Y(p.Percent))));
            poly.Add(new Point(X(maxT), Y(ordered[^1].Percent)));

            var curvePen = new Pen(LinuxTheme.Accent, 2.5);
            for (int i = 0; i < poly.Count - 1; i++)
                ctx.DrawLine(curvePen, poly[i], poly[i + 1]);

            foreach (var p in ordered)
            {
                var pt = new Point(X(p.TempC), Y(p.Percent));
                ctx.DrawEllipse(LinuxTheme.Accent, new Pen(Brushes.White, 1.5), pt, 4.5, 4.5);
            }
        }

        // Live CPU & GPU operating dots
        if (CpuTempC is { } ct && CpuPercent is { } cp)
        {
            var pt = new Point(X(ct), Y(cp));
            ctx.DrawEllipse(LinuxTheme.Value, new Pen(Brushes.White, 1.5), pt, 6, 6);
        }
        if (GpuTempC is { } gt && GpuPercent is { } gp)
        {
            var pt = new Point(X(gt), Y(gp));
            ctx.DrawEllipse(LinuxTheme.Ok, new Pen(Brushes.White, 1.5), pt, 5, 5);
        }
    }
}

/// <summary>
/// Interactive 3-zone keyboard preview (Left, Middle, Right). Clicking a zone selects it for individual colour editing.
/// </summary>
internal sealed class KeyboardPreviewControl : Control
{
    public Color LeftColor { get; set; } = Color.FromRgb(0, 255, 255);
    public Color MiddleColor { get; set; } = Color.FromRgb(0, 255, 255);
    public Color RightColor { get; set; } = Color.FromRgb(0, 255, 255);
    public int Brightness { get; set; } = 2;
    public int SelectedZone { get; set; } = -1; // -1 = all, 0 = left, 1 = middle, 2 = right

    public event Action<int>? ZoneClicked;

    public KeyboardPreviewControl()
    {
        Height = 130;
        MinWidth = 420;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        double w = Bounds.Width;
        if (w <= 0) return;
        double x = e.GetPosition(this).X;
        int zone = x < w / 3 ? 0 : x < 2 * w / 3 ? 1 : 2;
        ZoneClicked?.Invoke(zone);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 90 || h < 50) return;

        var outer = new Rect(0, 0, w, h);
        ctx.FillRectangle(LinuxTheme.Sidebar, outer, 8);
        ctx.DrawRectangle(new Pen(LinuxTheme.Border, 1.5), outer, 8);

        double pad = 12, gap = 10;
        double zoneW = (w - 2 * pad - 2 * gap) / 3.0;
        double zoneH = h - 2 * pad;
        Color[] cols = [LeftColor, MiddleColor, RightColor];
        string[] labels = ["Sol Bölge", "Orta Bölge", "Sağ Bölge"];
        byte alpha = Brightness switch { 0 => 35, 1 => 140, _ => 235 };

        for (int i = 0; i < 3; i++)
        {
            double x = pad + i * (zoneW + gap);
            var rect = new Rect(x, pad, zoneW, zoneH);
            var c = cols[i];
            var fill = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            bool selected = SelectedZone == -1 || SelectedZone == i;
            var borderPen = new Pen(selected ? Brushes.White : LinuxTheme.Border, selected ? 2 : 1);

            ctx.FillRectangle(fill, rect, 6);
            ctx.DrawRectangle(borderPen, rect, 6);

            // Keycap grid lines inside the zone
            var keyPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 20, 20, 24)), 1);
            for (int r = 1; r <= 3; r++)
            {
                double ky = pad + r * (zoneH / 4.0);
                ctx.DrawLine(keyPen, new Point(x + 6, ky), new Point(x + zoneW - 6, ky));
            }

            var ft = new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 13, Brushes.White);
            ctx.DrawText(ft, new Point(x + (zoneW - ft.Width) / 2, pad + (zoneH - ft.Height) / 2));
        }
    }
}
