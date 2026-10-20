using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Excalibur.Core.Settings;

namespace Excalibur.App.Tray;

/// <summary>
/// Draws the tray icon like the Windows volume/network icons: plain glyphs (the CPU temperature
/// or a symbol) in one color on a transparent background, in the user's <see cref="TrayFont"/>:
/// Bahnschrift (default) or the hand-drawn <see cref="PixelDigits"/> sets, which fall back to the
/// font at other sizes (scaled displays) or for "100". Owns the native icon handle and destroys
/// the previous one when it changes.
/// </summary>
internal sealed class TrayIconRenderer : IDisposable
{
    private Icon? _icon;
    private (string Text, Color Color, TrayFont Font) _drawn;

    /// <summary>
    /// The font fallback. Bahnschrift Bold (ships with Windows 10/11) is a narrow DIN face that stays
    /// solid at small sizes; the Segoe UI weights looked broken up (compared in docs/07).
    /// </summary>
    private static readonly string Family = new InstalledFontCollection().Families
        .Any(f => f.Name == "Bahnschrift") ? "Bahnschrift" : "Segoe UI";

    /// <summary>Returns the icon for this text and color, re-drawing only when either changed.</summary>
    public Icon Render(string text, Color color, TrayFont font)
    {
        if (_icon is not null && _drawn == (text, color, font)) return _icon;

        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        DigitStyle? pixel = font switch { TrayFont.PixelThin => DigitStyle.Thin, TrayFont.PixelBold => DigitStyle.Bold, _ => null };
        if (pixel is { } style && size == PixelDigits.Size && PixelDigits.CanDraw(text, style))
            PixelDigits.Draw(bmp, text, color, style);
        else
            DrawWithFont(bmp, text, color);

        var next = Icon.FromHandle(bmp.GetHicon());
        var old = _icon;
        _icon = next;
        _drawn = (text, color, font);
        if (old is not null) Destroy(old);
        return next;
    }

    private static void DrawWithFont(Bitmap bmp, string text, Color color)
    {
        int size = bmp.Width;
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // GenericTypographic carries LineLimit, which silently draws nothing when the line is
            // taller than the box; clear it. Shrink the font until the text fits the width.
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags = (format.FormatFlags & ~StringFormatFlags.LineLimit) | StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            float em = size * 0.94f;
            Font font = new(Family, em, FontStyle.Bold, GraphicsUnit.Pixel);
            while (em > 6 && g.MeasureString(text, font, PointF.Empty, format).Width > size)
            {
                font.Dispose();
                em -= 0.5f;
                font = new Font(Family, em, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using (font)
            using (var brush = new SolidBrush(color))
            {
                // Draw once to measure where the ink really is, then centre it vertically: fonts
                // differ in how much empty space they keep above and below digits.
                g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
                var (top, bottom) = InkRows(bmp);
                if (top < 0) return;
                g.Clear(Color.Transparent);
                float dy = (size - (bottom - top + 1)) / 2f - top;
                g.DrawString(text, font, brush, new RectangleF(0, dy, size, size), format);
            }
        }
    }

    /// <summary>The first and last row that contain visible pixels; (-1, -1) for an empty bitmap.</summary>
    private static (int Top, int Bottom) InkRows(Bitmap bmp)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 60)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
        return (top, bottom);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Destroy(Icon icon)
    {
        IntPtr handle = icon.Handle;
        icon.Dispose();
        Native.DestroyIcon(handle);
    }

    public void Dispose()
    {
        if (_icon is not null) Destroy(_icon);
        _icon = null;
    }
}
