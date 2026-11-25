using System.Windows;
using System.Windows.Media;
using AiUsage.Stats;

namespace AiUsage.Views.Controls;

/// <summary>
/// The filled part of a usage bar split into colored parts by token share (see <see
/// cref="WindowShares"/>): as wide as <see cref="UsedPercent"/> of the track, the largest part at the
/// left, rounded only at the two outer ends. Drawn in one pass, no element per part.
/// </summary>
public sealed class SegmentedBarFill : FrameworkElement
{

    public static readonly DependencyProperty UsedPercentProperty = DependencyProperty.Register(
        nameof(UsedPercent), typeof(double), typeof(SegmentedBarFill),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<BarSegment>), typeof(SegmentedBarFill),
        new FrameworkPropertyMetadata(Array.Empty<BarSegment>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public double UsedPercent
    {
        get => (double)GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public IReadOnlyList<BarSegment> Segments
    {
        get => (IReadOnlyList<BarSegment>)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }


    // A bar renders often: the clip is kept while the size stays the same.
    private RectangleGeometry? _clip;
    private double _clipWidth;
    private double _clipHeight;

    private RectangleGeometry ClipFor(double width, double height)
    {
        if (_clip is null || _clipWidth != width || _clipHeight != height)
        {
            // Fully rounded ends: the radius is half the bar's own height, like the track's.
            _clip = new RectangleGeometry(new Rect(0, 0, width, height), height / 2, height / 2);
            _clip.Freeze();
            _clipWidth = width;
            _clipHeight = height;
        }

        return _clip;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var filled = BarGeometry.FilledWidth(UsedPercent / 100.0, ActualWidth);
        if (filled <= 0 || ActualHeight <= 0)
            return;

        var placed = WindowShares.Layout(Segments, filled);
        drawingContext.PushClip(ClipFor(filled, ActualHeight));
        // Each part runs on to the right end and the next one paints over it: no seam of background
        // color shows between two parts whose edge falls inside a pixel.
        foreach (var part in placed)
        {
            drawingContext.DrawRectangle(ChartDrawCache.BrushFor(part.Color), null, new Rect(part.Left, 0, filled - part.Left, ActualHeight));
        }

        drawingContext.Pop();
    }
}
