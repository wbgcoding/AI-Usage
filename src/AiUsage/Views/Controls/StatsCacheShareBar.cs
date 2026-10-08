using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Views.Controls;

/// <summary>
/// Self-drawn cache-share bar - one horizontal stacked bar across the whole width, always
/// exactly four segments in a fixed order (new input, cache write, cache read, output), plus a
/// legend row underneath naming every segment's own percentage regardless of how narrow it drew.
/// </summary>
public sealed class StatsCacheShareBar : FrameworkElement
{
    public StatsCacheShareBar() => ClipToBounds = true;

    /// <summary>Always four values, in the fixed order <see cref="Stats.StatsCacheShareRow.AsValues"/>
    /// already returns them in.</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<long>), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata(Array.Empty<long>(), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public IReadOnlyList<long> Values
    {
        get => (IReadOnlyList<long>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (StatsCacheShareBar)d;
        var values = (IReadOnlyList<long>)e.NewValue;
        AutomationProperties.SetName(bar, BuildAccessibleSummary(values, bar.SegmentLabels));
    }

    private static void OnSegmentLabelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (StatsCacheShareBar)d;
        AutomationProperties.SetName(bar, BuildAccessibleSummary(bar.Values, (IReadOnlyList<string>)e.NewValue));
    }

    /// <summary>Pure so the summary text is unit testable without a visual tree.</summary>
    internal static string BuildAccessibleSummary(IReadOnlyList<long> values, IReadOnlyList<string> labels)
    {
        var total = Math.Max(1, values.Sum());
        return string.Join("; ", values.Select((value, i) =>
        {
            var name = i < labels.Count ? labels[i] : "?";
            var percent = value * 100.0 / total;
            return $"{name}: {StatsBarChart.PercentLabel(percent, CultureInfo.CurrentCulture)}";
        }));
    }

    public static readonly DependencyProperty SegmentBrushesProperty = DependencyProperty.Register(
        nameof(SegmentBrushes), typeof(IReadOnlyList<Brush>), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata(Array.Empty<Brush>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Brush> SegmentBrushes
    {
        get => (IReadOnlyList<Brush>)GetValue(SegmentBrushesProperty);
        set => SetValue(SegmentBrushesProperty, value);
    }

    public static readonly DependencyProperty SegmentLabelsProperty = DependencyProperty.Register(
        nameof(SegmentLabels), typeof(IReadOnlyList<string>), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentLabelsChanged));

    public IReadOnlyList<string> SegmentLabels
    {
        get => (IReadOnlyList<string>)GetValue(SegmentLabelsProperty);
        set => SetValue(SegmentLabelsProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>The same unit-word property <see cref="StatsBarChart.TokenWord"/> already carries,
    /// kept here for the same API shape even though this bar never draws an absolute token figure of
    /// its own - every inline label and every legend entry here is a percentage, and a percentage
    /// never takes a unit.</summary>
    public static readonly DependencyProperty TokenWordProperty = DependencyProperty.Register(
        nameof(TokenWord), typeof(string), typeof(StatsCacheShareBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string TokenWord
    {
        get => (string)GetValue(TokenWordProperty);
        set => SetValue(TokenWordProperty, value);
    }

    /// <summary>Every segment's own pixel width across <paramref name="totalWidth"/>, proportional to
    /// its share of the four values' sum - the last boundary is always pinned to exactly <paramref
    /// name="totalWidth"/>, so the returned widths always sum to it exactly regardless of rounding.
    /// Pure so this is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<double> SegmentWidths(IReadOnlyList<long> values, double totalWidth)
    {
        var total = values.Sum();
        if (total <= 0 || totalWidth <= 0)
            return values.Select(_ => 0.0).ToList();

        var widths = new double[values.Count];
        var cumulative = 0L;
        var previousBoundary = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            cumulative += Math.Max(0, values[i]);
            var boundary = i == values.Count - 1 ? totalWidth : cumulative / (double)total * totalWidth;
            widths[i] = boundary - previousBoundary;
            previousBoundary = boundary;
        }
        return widths;
    }

    private const double LegendRowHeight = 18;
    private const double BarHeight = 24;
    private const double LegendGap = 8;
    private const double LegendSwatch = 8;
    private const double LegendItemSpacing = 24;

    /// <summary>Where each legend entry goes: entries run left to right and an entry that would
    /// reach past <paramref name="availableWidth"/> starts a new row, so a narrow bar never cuts a
    /// name off. The first entry of a row always stays on it, however wide. Pure so the wrapping is
    /// unit testable.</summary>
    internal static IReadOnlyList<(double X, int Row)> LegendPositions(IReadOnlyList<double> entryWidths, double availableWidth)
    {
        var positions = new List<(double X, int Row)>(entryWidths.Count);
        var x = 0.0;
        var row = 0;
        foreach (var entryWidth in entryWidths)
        {
            if (x > 0 && x + entryWidth > availableWidth)
            {
                x = 0;
                row++;
            }
            positions.Add((x, row));
            x += entryWidth + LegendItemSpacing;
        }
        return positions;
    }

    private List<FormattedText> LegendTexts(double pixelsPerDip)
    {
        var values = Values;
        var labels = SegmentLabels;
        var total = values.Sum();
        var typeface = new Typeface("Segoe UI");
        var texts = new List<FormattedText>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = i < labels.Count ? labels[i] : "?";
            var percent = total > 0 ? values[i] * 100.0 / total : 0;
            var text = $"{name} · {StatsBarChart.PercentLabel(percent, CultureInfo.CurrentCulture)}";
            texts.Add(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, TextBrush, pixelsPerDip));
        }
        return texts;
    }

    private static List<double> EntryWidths(List<FormattedText> texts) =>
        texts.Select(text => LegendSwatch + 4 + text.Width).ToList();

    /// <summary>Tall enough for the bar plus every legend row the current width needs.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var rows = 1;
        if (Values.Count > 0 && Values.Sum() > 0)
        {
            var positions = LegendPositions(EntryWidths(LegendTexts(VisualTreeHelper.GetDpi(this).PixelsPerDip)), width);
            rows = positions.Count == 0 ? 1 : positions[^1].Row + 1;
        }
        return new Size(0, BarHeight + LegendGap + rows * LegendRowHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var values = Values;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        var total = values.Sum();
        if (values.Count == 0 || total <= 0)
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                var formatted = new FormattedText(
                    EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, TextBrush, dpi);
                drawingContext.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2));
            }
            return;
        }

        const double barHeight = BarHeight;
        var barWidth = width;
        var widths = SegmentWidths(values, barWidth);
        var brushes = SegmentBrushes;

        var x = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var segmentWidth = widths[i];
            var brush = i < brushes.Count ? brushes[i] : Brushes.Gray;
            if (segmentWidth > 0)
                drawingContext.DrawRectangle(brush, null, new Rect(x, 0, segmentWidth, barHeight));

            x += segmentWidth;
        }

        // The legend: every segment named with its own percentage - the only place the bar's
        // numbers appear, so a narrow segment never loses its figure.
        var legendTexts = LegendTexts(dpi);
        var positions = LegendPositions(EntryWidths(legendTexts), width);
        for (var i = 0; i < legendTexts.Count; i++)
        {
            var brush = i < brushes.Count ? brushes[i] : Brushes.Gray;
            var (legendX, row) = positions[i];
            var legendY = BarHeight + LegendGap + row * LegendRowHeight;
            drawingContext.DrawRectangle(brush, null, new Rect(legendX, legendY + (LegendRowHeight - LegendSwatch) / 2, LegendSwatch, LegendSwatch));
            var formatted = legendTexts[i];
            drawingContext.DrawText(formatted, new Point(legendX + LegendSwatch + 4, legendY + (LegendRowHeight - formatted.Height) / 2));
        }
    }
}
