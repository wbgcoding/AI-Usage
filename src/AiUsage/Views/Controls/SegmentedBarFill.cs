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
    /// <summary>Same floor the plain fill keeps (LevelFillStyle in UsageBar.xaml), so a barely used window
    /// still shows a mark.</summary>
    private const double MinFilledWidth = 3;

    /// <summary>Half the 8 px bar height, the corner radius of the track and the plain fill.</summary>
    private const double CornerRadius = 4;

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

    /// <summary>The width of the filled part on a track <paramref name="trackWidth"/> wide.</summary>
    internal static double FilledWidth(double usedPercent, double trackWidth)
    {
        if (usedPercent <= 0 || trackWidth <= 0)
            return 0;
        return Math.Min(trackWidth, Math.Max(MinFilledWidth, trackWidth * Math.Clamp(usedPercent, 0, 100) / 100.0));
    }

    // The palette is a handful of colors and a bar renders often: brushes are frozen once per color,
    // and the clip is kept while the size stays the same.
    private static readonly Dictionary<Color, SolidColorBrush> Brushes = [];

    private RectangleGeometry? _clip;
    private double _clipWidth;
    private double _clipHeight;

    private static SolidColorBrush BrushFor(Color color)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                Brushes[color] = brush;
            }

            return brush;
        }
    }

    private RectangleGeometry ClipFor(double width, double height)
    {
        if (_clip is null || _clipWidth != width || _clipHeight != height)
        {
            _clip = new RectangleGeometry(new Rect(0, 0, width, height), CornerRadius, CornerRadius);
            _clip.Freeze();
            _clipWidth = width;
            _clipHeight = height;
        }

        return _clip;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var filled = FilledWidth(UsedPercent, ActualWidth);
        if (filled <= 0 || ActualHeight <= 0)
            return;

        var placed = WindowShares.Layout(Segments, filled);
        drawingContext.PushClip(ClipFor(filled, ActualHeight));
        // Each part runs on to the right end and the next one paints over it: no seam of background
        // color shows between two parts whose edge falls inside a pixel.
        foreach (var part in placed)
        {
            drawingContext.DrawRectangle(BrushFor(part.Color), null, new Rect(part.Left, 0, filled - part.Left, ActualHeight));
        }

        drawingContext.Pop();
    }
}
