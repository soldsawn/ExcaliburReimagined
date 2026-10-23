using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// The keyboard drawn key by key, each key in its zone's colour (docs/09: Razer Synapse style). The three zones split
/// the width in thirds, as the firmware's three light zones do on the G870 (left, middle, right incl. the number pad;
/// checked by eye 2026-10-23). A click picks a zone; <see cref="Selected"/> -1 means the whole keyboard.
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

    private Color[] _colours = [Colors.Cyan, Colors.Cyan, Colors.Cyan];
    private bool _lit = true;
    private int _selected = -1;

    public event Action<int>? ZoneClicked;

    /// <summary>Left, middle, right.</summary>
    public Color[] Colours { get => _colours; set { _colours = value; InvalidateVisual(); } }

    /// <summary>False draws the keys unlit (light off).</summary>
    public bool Lit { get => _lit; set { _lit = value; InvalidateVisual(); } }

    /// <summary>0-2 a zone, -1 the whole keyboard.</summary>
    public int Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

    public KeyboardView()
    {
        Cursor = Cursors.Hand;
        MinHeight = 200;
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 640 : available.Width;
        return new Size(w, Math.Max(MinHeight, w * 0.34));
    }

    private double Unit => (ActualWidth - 2 * Pad) / TotalUnits;
    private const double Pad = 14;

    private static int ZoneAt(double units) => units < TotalUnits / 3 ? 0 : units < 2 * TotalUnits / 3 ? 1 : 2;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        double u = (e.GetPosition(this).X - Pad) / Unit;
        if (u >= 0 && u <= TotalUnits) ZoneClicked?.Invoke(ZoneAt(u));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double unit = Unit;
        if (unit <= 0) return;
        double rowH = (ActualHeight - 2 * Pad) / MainRows.Length, gap = Math.Max(2, unit * 0.12);
        var deck = (Brush)FindResource("Sidebar");
        var edge = new Pen((Brush)FindResource("Border"), 1);
        dc.DrawRoundedRectangle(deck, edge, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 12, 12);

        var off = (Brush)FindResource("Card");
        var brushes = _colours.Select(c => (Brush)new SolidColorBrush(Color.FromArgb(_lit ? (byte)230 : (byte)60, c.R, c.G, c.B))).ToArray();
        void Key(double x, double y, double w)
        {
            int zone = ZoneAt(x + w / 2);
            var r = new Rect(Pad + x * unit + gap / 2, Pad + y * rowH + gap / 2, w * unit - gap, rowH - gap);
            dc.DrawRoundedRectangle(off, null, r, 3, 3);
            dc.DrawRoundedRectangle(brushes[zone], null, r, 3, 3);
        }
        for (int row = 0; row < MainRows.Length; row++)
        {
            double x = 0;
            foreach (double w in MainRows[row]) { Key(x, row, w); x += w; }
            for (int k = 0; k < PadUnits; k++) Key(MainUnits + PadGap + k, row, 1);
        }

        double third = TotalUnits / 3;
        // Faint zone borders, so the zones show even when all three have one colour.
        var divider = new Pen((Brush)FindResource("SubText"), 1) { DashStyle = DashStyles.Dash };
        for (int z = 1; z < 3; z++)
        {
            double x = Math.Round(Pad + z * third * unit) + 0.5;
            dc.DrawLine(divider, new Point(x, Pad - 6), new Point(x, ActualHeight - Pad + 6));
        }
        if (_selected >= 0)
        {
            var accent = new Pen((Brush)FindResource("Accent"), 2);
            dc.DrawRoundedRectangle(null, accent, new Rect(Pad + _selected * third * unit - 3, Pad - 4, third * unit + 6, ActualHeight - 2 * Pad + 8), 6, 6);
        }
    }
}
