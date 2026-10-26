using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Excalibur.Core.Lighting;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// The keyboard as a backlit deck (docs/09, Razer / Aura style): dark keys over a glow in each zone's colour that shows
/// through the gaps and bleeds into the next zone, as the real light does. The three zones split the width in thirds
/// (left, middle, right incl. the number pad; checked by eye 2026-10-23). A click picks a zone; <see cref="Selected"/>
/// -1 means the whole keyboard. Breathing and colour cycle are previewed with a timer that runs only while the control
/// is on screen: the tray process never builds it, and a closed window or another page stops it.
/// </summary>
internal sealed class KeyboardView : FrameworkElement
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

    private static readonly Brush KeyFill = Frozen(new LinearGradientBrush(Color.FromRgb(30, 30, 34), Color.FromRgb(18, 18, 21), 90));
    private static readonly Brush Deck = Frozen(new SolidColorBrush(Color.FromRgb(12, 12, 14)));

    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DateTime _start = DateTime.Now;
    private Color[] _colours = [Colors.Cyan, Colors.Cyan, Colors.Cyan];
    private LightEffect _effect = LightEffect.Static;
    private int _brightness = 2, _selected = -1;

    public event Action<int>? ZoneClicked;

    public KeyboardView()
    {
        Cursor = Cursors.Hand;
        _animation.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    /// <summary>Left, middle, right.</summary>
    public Color[] Colours { get => _colours; set { _colours = value; InvalidateVisual(); } }

    public LightEffect Effect { get => _effect; set { _effect = value; UpdateAnimation(); InvalidateVisual(); } }

    /// <summary>0 = light off, 1 low, 2 high.</summary>
    public int Brightness { get => _brightness; set { _brightness = value; UpdateAnimation(); InvalidateVisual(); } }

    /// <summary>0-2 a zone, -1 the whole keyboard.</summary>
    public int Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

    private bool Animated => IsVisible && _brightness > 0 && _effect is LightEffect.Breathing or LightEffect.ColorCycle;

    private void UpdateAnimation()
    {
        if (Animated) _animation.Start();
        else _animation.Stop();
    }

    // Height / width of the deck. The height is capped, so on a wide window the deck keeps this shape and sits in the middle
    // instead of stretching the keys sideways (full screen, 2026-10-26).
    private const double Aspect = 0.27;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 640 : available.Width;
        return new Size(w, Math.Clamp(w * Aspect, 150, 230));
    }

    /// <summary>The deck's width and left edge inside the control.</summary>
    private double DeckWidth => Math.Min(ActualWidth, ActualHeight / Aspect);
    private double DeckLeft => (ActualWidth - DeckWidth) / 2;

    private double Unit => (DeckWidth - 2 * Pad) / TotalUnits;

    private static int ZoneAt(double units) => units < TotalUnits / 3 ? 0 : units < 2 * TotalUnits / 3 ? 1 : 2;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        double u = (e.GetPosition(this).X - DeckLeft - Pad) / Unit;
        if (u >= 0 && u <= TotalUnits) ZoneClicked?.Invoke(ZoneAt(u));
    }

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

    protected override void OnRender(DrawingContext dc)
    {
        double unit = Unit;
        if (unit <= 0) return;
        var (colours, level) = Frame();
        double width = DeckWidth;
        dc.PushTransform(new TranslateTransform(DeckLeft, 0));
        var bounds = new Rect(0, 0, width, ActualHeight);
        dc.DrawRoundedRectangle(Deck, new Pen((Brush)FindResource("Border"), 1), new Rect(0.5, 0.5, width - 1, ActualHeight - 1), 14, 14);
        dc.PushClip(new RectangleGeometry(bounds, 14, 14));

        // The glow under the keys: one soft ellipse per zone, wide enough to bleed into its neighbours.
        double third = (width - 2 * Pad) / 3;
        if (level > 0)
            for (int z = 0; z < 3; z++)
            {
                var c = colours[z];
                var glow = new RadialGradientBrush(
                    Color.FromArgb((byte)(200 * level), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
                { GradientOrigin = new Point(0.5, 0.5) };
                glow.GradientStops[0].Offset = 0.15;
                double cx = Pad + third * (z + 0.5);
                dc.DrawEllipse(glow, null, new Point(cx, ActualHeight / 2), third * 0.95, ActualHeight * 0.75);
            }

        double rowH = (ActualHeight - 2 * Pad) / MainRows.Length, gap = Math.Max(3, unit * 0.16);
        var edges = colours.Select(c => (Pen)Frozen(new Pen(new SolidColorBrush(level > 0
            ? Color.FromArgb((byte)(70 + 170 * level), c.R, c.G, c.B) : Color.FromRgb(58, 58, 64)), 1.2))).ToArray();
        void Key(double x, double y, double w)
        {
            int zone = ZoneAt(x + w / 2);
            var r = new Rect(Pad + x * unit + gap / 2, Pad + y * rowH + gap / 2, w * unit - gap, rowH - gap);
            dc.DrawRoundedRectangle(KeyFill, edges[zone], r, 4, 4);
        }
        for (int row = 0; row < MainRows.Length; row++)
        {
            double x = 0;
            foreach (double w in MainRows[row]) { Key(x, row, w); x += w; }
            for (int k = 0; k < PadUnits; k++) Key(MainUnits + PadGap + k, row, 1);
        }
        dc.Pop();

        if (_selected >= 0 && _effect == LightEffect.Static)
        {
            var accent = new Pen((Brush)FindResource("Accent"), 2);
            dc.DrawRoundedRectangle(null, accent, new Rect(Pad + _selected * third - 4, Pad - 6, third + 8, ActualHeight - 2 * Pad + 12), 8, 8);
        }
        dc.Pop();
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
