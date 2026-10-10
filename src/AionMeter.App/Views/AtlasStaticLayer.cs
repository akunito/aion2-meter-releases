using System.Windows;
using System.Windows.Media;

namespace AionMeter.App.Views;

/// <summary>
/// One element that paints the atlas layers which never change between ticks (seeded nodes, pet spawns) straight to a
/// DrawingContext. Before this every 500 ms tick rebuilt them as thousands of Ellipse/Image controls (5,474 seeded nodes plus up
/// to 13k pet points on Verteron zoomed out), which is what made panning stutter (Diego, 2026-10-11). Repainted only on
/// pan/zoom or when its data changes (InvalidateVisual).
/// </summary>
public sealed class AtlasStaticLayer : FrameworkElement
{
    public Action<DrawingContext>? Draw { get; set; }
    protected override void OnRender(DrawingContext dc) => Draw?.Invoke(dc);
}
