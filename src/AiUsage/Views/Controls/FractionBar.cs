using System.Windows;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// A slim horizontal gauge: a rounded track filled from the left to <see cref="Fraction"/> (0..1) of its
/// width. Drawn in one pass; the brushes come from the theme, the fill is usually the provider colour.
/// </summary>
public sealed class FractionBar : FrameworkElement
{
    /// <summary>Same floor the usage bars keep, so a barely used window still shows a mark.</summary>
    private const double MinFilledWidth = 3;

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(FractionBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FractionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(FractionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(Brush), typeof(FractionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The theme's track outline width (0 in the literal themes, 1 in high contrast).</summary>
    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness), typeof(Thickness), typeof(FractionBar),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush? Outline
    {
        get => (Brush?)GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    public Thickness OutlineThickness
    {
        get => (Thickness)GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    /// <summary>The width of the filled part on a track <paramref name="trackWidth"/> wide.</summary>
    internal static double FilledWidth(double fraction, double trackWidth)
    {
        if (!(fraction > 0) || trackWidth <= 0)
            return 0;
        return Math.Min(trackWidth, Math.Max(MinFilledWidth, trackWidth * Math.Min(fraction, 1)));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Height is > 0 and < double.PositiveInfinity ? Height : 8);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var radius = height / 2;
        var outline = Outline is { } brush && OutlineThickness.Left > 0 ? new Pen(brush, OutlineThickness.Left) : null;
        drawingContext.DrawRoundedRectangle(Track, outline, new Rect(0, 0, width, height), radius, radius);

        var filled = FilledWidth(Fraction, width);
        if (filled <= 0 || Fill is null)
            return;

        // A fill shorter than the bar is tall cannot be rounder than half its own width.
        var fillRadius = Math.Min(radius, filled / 2);
        drawingContext.DrawRoundedRectangle(Fill, null, new Rect(0, 0, filled, height), fillRadius, fillRadius);
    }
}
