using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Excalibur.App.Ui.Controls;

/// <summary>
/// A card with a small accent-colored corner bracket on two opposite corners (top left, bottom right),
/// the one touch borrowed from Armoury Crate (docs/09). Everything else is a plain <see cref="Border"/>.
/// </summary>
internal sealed class BracketCard : Border
{
    private const double Arm = 12, Thickness = 1.5;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (TryFindResource("Accent") is not Brush accent || ActualWidth < 2 * Arm || ActualHeight < 2 * Arm) return;
        var pen = new Pen(accent, Thickness) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
        double w = ActualWidth, h = ActualHeight, o = Thickness / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(o, Arm), false, false);
            ctx.LineTo(new Point(o, o), true, false);
            ctx.LineTo(new Point(Arm, o), true, false);
            ctx.BeginFigure(new Point(w - Arm, h - o), false, false);
            ctx.LineTo(new Point(w - o, h - o), true, false);
            ctx.LineTo(new Point(w - o, h - Arm), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
