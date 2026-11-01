using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>
/// A card with a small accent-coloured corner bracket on two opposite corners (top left, bottom right), as on Windows
/// (Excalibur.App/Ui/Controls/BracketCard.cs). Avalonia's Border cannot be drawn over, so this draws the card itself;
/// it takes Border's own properties, so the CardBox style sets both.
/// </summary>
internal sealed class BracketCard : Decorator
{
    private const double Arm = 12, Stroke = 1.5;

    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<BracketCard>();
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = Border.BorderBrushProperty.AddOwner<BracketCard>();
    public static readonly StyledProperty<Thickness> BorderThicknessProperty = Border.BorderThicknessProperty.AddOwner<BracketCard>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<BracketCard>();

    static BracketCard() => AffectsRender<BracketCard>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty, CornerRadiusProperty);

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Thickness BorderThickness { get => GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height, line = BorderThickness.Left;
        var rect = new Rect(0, 0, w, h).Deflate(line / 2);
        dc.DrawRectangle(Background, BorderBrush is { } b && line > 0 ? new Pen(b, line) : null, new RoundedRect(rect, CornerRadius.TopLeft));

        if (this.TryFindResource("CornerMarks", ActualThemeVariant, out var marks) && marks is false) return; // turned off in Settings
        if (!this.TryFindResource("Accent", ActualThemeVariant, out var a) || a is not IBrush accent) return;
        if (w < 2 * Arm || h < 2 * Arm) return;
        double o = Stroke / 2;
        var pen = new Pen(accent, Stroke, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Miter);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(o, Arm), false);
            ctx.LineTo(new Point(o, o));
            ctx.LineTo(new Point(Arm, o));
            ctx.EndFigure(false);
            ctx.BeginFigure(new Point(w - Arm, h - o), false);
            ctx.LineTo(new Point(w - o, h - o));
            ctx.LineTo(new Point(w - o, h - Arm));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}
