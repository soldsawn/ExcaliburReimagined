using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Excalibur.Core.Lighting;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>
/// The keyboard as a backlit deck, as on Windows: dark keys over a glow in each zone's colour that shows through the gaps and
/// bleeds into the next zone, as the real light does. The three zones split the width in thirds. A click picks a zone;
/// <see cref="Selected"/> -1 means the whole keyboard. Breathing and colour cycle are previewed with a timer that runs only
/// while the control is on screen.
/// </summary>
internal sealed class KeyboardView : Control
{
    // Key widths in key units per row: the main block (15 u) and the number pad (4 u) beside it.
    private static readonly double[][] MainRows =
    [
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2],
        [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5],
        [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25],
        [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75],
        [1.25, 1, 1.25, 1.25, 4.75, 1.25, 1.25, 1, 1, 1],
    ];
    private const double MainUnits = 15, PadGap = 0.5, PadUnits = 4, TotalUnits = MainUnits + PadGap + PadUnits;
    private const double Pad = 18;
    // Height / width of the deck; capped so a wide window does not stretch the keys sideways.
    private const double Aspect = 0.27;

    private static readonly IBrush KeyFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(30, 30, 34), 0), new GradientStop(Color.FromRgb(18, 18, 21), 1) },
    }.ToImmutable();
    private static readonly IBrush Deck = new ImmutableSolidColorBrush(Color.FromRgb(12, 12, 14));

    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DateTime _start = DateTime.Now;
    private Color[] _colours = [Colors.Cyan, Colors.Cyan, Colors.Cyan];
    private LightEffect _effect = LightEffect.Static;
    private int _brightness = 2, _selected = -1;
    private bool _onScreen;

    public event Action<int>? ZoneClicked;

    public KeyboardView()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        _animation.Tick += (_, _) => InvalidateVisual();
        AttachedToVisualTree += (_, _) => { _onScreen = true; UpdateAnimation(); };
        DetachedFromVisualTree += (_, _) => { _onScreen = false; UpdateAnimation(); };
        PointerReleased += (_, e) =>
        {
            double u = (e.GetPosition(this).X - DeckLeft - Pad) / Unit;
            if (IsEnabled && u >= 0 && u <= TotalUnits) ZoneClicked?.Invoke(ZoneAt(u));
        };
    }

    /// <summary>Left, middle, right.</summary>
    public Color[] Colours { get => _colours; set { _colours = value; InvalidateVisual(); } }

    public new LightEffect Effect { get => _effect; set { _effect = value; UpdateAnimation(); InvalidateVisual(); } }

    /// <summary>0 = light off, 1 low, 2 high.</summary>
    public int Brightness { get => _brightness; set { _brightness = value; UpdateAnimation(); InvalidateVisual(); } }

    /// <summary>0-2 a zone, -1 the whole keyboard.</summary>
    public int Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

    private bool Animated => _onScreen && _brightness > 0 && _effect is LightEffect.Breathing or LightEffect.ColorCycle;

    private void UpdateAnimation()
    {
        if (Animated) _animation.Start();
        else _animation.Stop();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 640 : available.Width;
        return new Size(w, Math.Clamp(w * Aspect, 150, 230));
    }

    /// <summary>The deck's width and left edge inside the control.</summary>
    private double DeckWidth => Math.Min(Bounds.Width, Bounds.Height / Aspect);
    private double DeckLeft => (Bounds.Width - DeckWidth) / 2;

    private double Unit => (DeckWidth - 2 * Pad) / TotalUnits;

    private static int ZoneAt(double units) => units < TotalUnits / 3 ? 0 : units < 2 * TotalUnits / 3 ? 1 : 2;

    /// <summary>The colours and light level shown now: the effect's animation applied.</summary>
    private (Color[] Colours, double Level) Frame()
    {
        double t = (DateTime.Now - _start).TotalSeconds;
        double level = _brightness == 0 ? 0 : _brightness == 1 ? 0.55 : 1;
        return _effect switch
        {
            LightEffect.Breathing => (_colours, level * (0.15 + 0.85 * (0.5 - 0.5 * Math.Cos(t * Math.PI / 2)))),
            LightEffect.ColorCycle => (new[] { 0, 1, 2 }.Select(z => ColourWheel.FromHsv((t * 40 + z * 40) % 360, 1, 1)).ToArray(), level),
            LightEffect.Off => (_colours, 0),
            _ => (_colours, level),
        };
    }

    public override void Render(DrawingContext dc)
    {
        double unit = Unit;
        if (unit <= 0) return;
        var (colours, level) = Frame();
        double width = DeckWidth, height = Bounds.Height;
        using (dc.PushTransform(Matrix.CreateTranslation(DeckLeft, 0)))
        {
            var bounds = new Rect(0, 0, width, height);
            dc.DrawRectangle(Deck, new Pen(Palette.Brush(this, "Border"), 1), new RoundedRect(new Rect(0.5, 0.5, width - 1, height - 1), 14));
            double third = (width - 2 * Pad) / 3;
            using (dc.PushClip(new RoundedRect(bounds, 14)))
            {
                // The glow under the keys: one soft ellipse per zone, wide enough to bleed into its neighbours.
                if (level > 0)
                    for (int z = 0; z < 3; z++)
                    {
                        var c = colours[z];
                        var glow = new RadialGradientBrush
                        {
                            GradientStops =
                            {
                                new GradientStop(Color.FromArgb((byte)(200 * level), c.R, c.G, c.B), 0.15),
                                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                            },
                        };
                        double cx = Pad + third * (z + 0.5);
                        dc.DrawEllipse(glow, null, new Point(cx, height / 2), third * 0.95, height * 0.75);
                    }

                double rowH = (height - 2 * Pad) / MainRows.Length, gap = Math.Max(3, unit * 0.16);
                var edges = colours.Select(c => new Pen(new SolidColorBrush(level > 0
                    ? Color.FromArgb((byte)(70 + 170 * level), c.R, c.G, c.B) : Color.FromRgb(58, 58, 64)), 1.2)).ToArray();
                void Key(double x, double y, double w)
                {
                    int zone = ZoneAt(x + w / 2);
                    var r = new Rect(Pad + x * unit + gap / 2, Pad + y * rowH + gap / 2, w * unit - gap, rowH - gap);
                    dc.DrawRectangle(KeyFill, edges[zone], new RoundedRect(r, 4));
                }
                for (int row = 0; row < MainRows.Length; row++)
                {
                    double x = 0;
                    foreach (double w in MainRows[row]) { Key(x, row, w); x += w; }
                    for (int k = 0; k < PadUnits; k++) Key(MainUnits + PadGap + k, row, 1);
                }
            }

            if (_selected >= 0 && _effect == LightEffect.Static)
            {
                var accent = new Pen(Palette.Brush(this, "Accent"), 2);
                dc.DrawRectangle(null, accent, new RoundedRect(new Rect(Pad + _selected * third - 4, Pad - 6, third + 8, height - 2 * Pad + 12), 8));
            }
        }
    }
}
