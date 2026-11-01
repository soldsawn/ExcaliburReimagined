using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Excalibur.Linux.Ui.Controls;

/// <summary>A fan duty bar made of blocks (the Windows window's look): lit blocks up to <see cref="Percent"/>.</summary>
internal sealed class BlockBar : Control
{
    public static readonly StyledProperty<int?> PercentProperty = AvaloniaProperty.Register<BlockBar, int?>(nameof(Percent));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<BlockBar, IBrush?>(nameof(Fill), Brushes.DeepSkyBlue);
    public static readonly StyledProperty<IBrush?> EmptyProperty = AvaloniaProperty.Register<BlockBar, IBrush?>(nameof(Empty), Brushes.DimGray);

    static BlockBar() => AffectsRender<BlockBar>(PercentProperty, FillProperty, EmptyProperty);

    public int? Percent { get => GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Empty { get => GetValue(EmptyProperty); set => SetValue(EmptyProperty, value); }
    public int Blocks { get; set; } = 24;

    protected override Size MeasureOverride(Size available) => new(0, 8);

    public override void Render(DrawingContext dc)
    {
        double gap = 3, w = (Bounds.Width - (Blocks - 1) * gap) / Blocks;
        if (w <= 0) return;
        int lit = Percent is { } p ? (int)Math.Round(Math.Clamp(p, 0, 100) / 100.0 * Blocks) : 0;
        for (int i = 0; i < Blocks; i++)
            dc.DrawRectangle(i < lit ? Fill : Empty, null, new RoundedRect(new Rect(i * (w + gap), 0, w, Bounds.Height), 1.5));
    }
}
