using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// A colour picker in the Aura / Synapse style: a hue ring, and inside it a square of saturation (left to right) and
/// brightness (top to bottom) for that hue. Dragging raises <see cref="Picking"/> on every move (for a live preview) and
/// <see cref="Picked"/> once on release (to send).
/// </summary>
internal sealed class ColourWheel : FrameworkElement
{
    private const double RingWidth = 18;
    private double _hue, _sat = 1, _val = 1;
    private WriteableBitmap? _ring, _square;
    private double _squareHue = -1;
    private enum Drag { None, Ring, Square }
    private Drag _drag;

    public event Action<Color>? Picking;
    public event Action<Color>? Picked;

    public ColourWheel()
    {
        Width = Height = 190;
        Cursor = Cursors.Cross;
    }

    public Color Colour
    {
        get => FromHsv(_hue, _sat, _val);
        set
        {
            if (_drag != Drag.None) return; // the user is dragging: their colour wins
            (_hue, _sat, _val) = ToHsv(value, _hue);
            InvalidateVisual();
        }
    }

    private double Size => Math.Min(ActualWidth, ActualHeight);
    private Point Centre => new(ActualWidth / 2, ActualHeight / 2);
    private double InnerRadius => Size / 2 - RingWidth;
    /// <summary>Half the side of the square that fits inside the ring, with a small gap.</summary>
    private double Half => (InnerRadius - 6) / Math.Sqrt(2);

    protected override void OnRender(DrawingContext dc)
    {
        int size = (int)Size;
        if (size <= 2 * RingWidth) return;
        if (_ring is null || _ring.PixelWidth != size) _ring = RenderRing(size);
        var c = Centre;
        dc.DrawImage(_ring, new Rect(c.X - size / 2.0, c.Y - size / 2.0, size, size));

        int side = Math.Max(2, (int)(2 * Half));
        if (_square is null || _square.PixelWidth != side || _squareHue != _hue) { _square = RenderSquare(side, _hue); _squareHue = _hue; }
        var sq = new Rect(c.X - Half, c.Y - Half, 2 * Half, 2 * Half);
        dc.DrawImage(_square, sq);

        // Markers: a ring on the hue, a ring on the saturation/brightness.
        double a = _hue * Math.PI / 180, r = Size / 2 - RingWidth / 2;
        var hueAt = new Point(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a));
        var svAt = new Point(sq.Left + _sat * sq.Width, sq.Top + (1 - _val) * sq.Height);
        var white = new Pen(Brushes.White, 2.5);
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
        foreach (var p in new[] { hueAt, svAt })
        {
            dc.DrawEllipse(null, dark, p, 8, 8);
            dc.DrawEllipse(new SolidColorBrush(Colour), white, p, 6.5, 6.5);
        }
    }

    private static WriteableBitmap RenderRing(int size)
    {
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        var px = new int[size * size];
        double outer = size / 2.0, inner = outer - RingWidth, cx = outer - 0.5;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - cx, dy = cx - y, d = Math.Sqrt(dx * dx + dy * dy);
                // One pixel of soft edge on both sides of the ring.
                double alpha = Math.Clamp(Math.Min(outer - d, d - inner), 0, 1);
                if (alpha <= 0) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                px[y * size + x] = Premultiplied(FromHsv(hue, 1, 1), alpha);
            }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);
        return bmp;
    }

    private static WriteableBitmap RenderSquare(int side, double hue)
    {
        var bmp = new WriteableBitmap(side, side, 96, 96, PixelFormats.Pbgra32, null);
        var px = new int[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                px[y * side + x] = Premultiplied(FromHsv(hue, x / (side - 1.0), 1 - y / (side - 1.0)), 1);
        bmp.WritePixels(new Int32Rect(0, 0, side, side), px, side * 4, 0);
        return bmp;
    }

    private static int Premultiplied(Color c, double alpha) =>
        (int)(alpha * 255) << 24 | (int)(c.R * alpha) << 16 | (int)(c.G * alpha) << 8 | (int)(c.B * alpha);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var c = Centre;
        double d = (p - c).Length;
        if (d >= InnerRadius - 2 && d <= Size / 2 + 2) _drag = Drag.Ring;
        else if (Math.Abs(p.X - c.X) <= Half + 4 && Math.Abs(p.Y - c.Y) <= Half + 4) _drag = Drag.Square;
        else return;
        CaptureMouse();
        Move(p);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag != Drag.None) Move(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        ReleaseMouseCapture();
        Picked?.Invoke(Colour);
    }

    private void Move(Point p)
    {
        var c = Centre;
        if (_drag == Drag.Ring) _hue = (Math.Atan2(c.Y - p.Y, p.X - c.X) * 180 / Math.PI + 360) % 360;
        else
        {
            _sat = Math.Clamp((p.X - (c.X - Half)) / (2 * Half), 0, 1);
            _val = Math.Clamp(1 - (p.Y - (c.Y - Half)) / (2 * Half), 0, 1);
        }
        InvalidateVisual();
        Picking?.Invoke(Colour);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>The colour as hue, saturation, value; a grey keeps <paramref name="keepHue"/> (it has no hue of its own).</summary>
    public static (double H, double S, double V) ToHsv(Color colour, double keepHue = 0)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? keepHue
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
