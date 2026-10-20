using System.Windows;
using System.Windows.Media;

namespace Excalibur.App.Ui.Controls;

/// <summary>A fan duty bar made of blocks (the tray panel's look): lit blocks up to <see cref="Percent"/>.</summary>
internal sealed class BlockBar : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int?), typeof(BlockBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BlockBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyProperty = DependencyProperty.Register(
        nameof(Empty), typeof(Brush), typeof(BlockBar), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Empty { get => (Brush)GetValue(EmptyProperty); set => SetValue(EmptyProperty, value); }
    public int Blocks { get; set; } = 24;

    protected override Size MeasureOverride(Size available) => new(0, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double gap = 3, w = (ActualWidth - (Blocks - 1) * gap) / Blocks;
        if (w <= 0) return;
        int lit = Percent is { } p ? (int)Math.Round(Math.Clamp(p, 0, 100) / 100.0 * Blocks) : 0;
        for (int i = 0; i < Blocks; i++)
            dc.DrawRoundedRectangle(i < lit ? Fill : Empty, null, new Rect(i * (w + gap), 0, w, ActualHeight), 1.5, 1.5);
    }
}
