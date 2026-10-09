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
/// Self-drawn donut chart - the same bare-<see cref="FrameworkElement"/> construction as <see
/// cref="StatsBarChart"/>. Each slice is one stroked <see cref="ArcSegment"/> on a
/// <see cref="PathGeometry"/>, never a filled pie wedge - a ring, in from the outer stroke width the
/// same way a real donut chart reads. The total is drawn in the middle; a legend naming every
/// slice's colour, label and percentage is drawn to the right of the ring, in the same
/// <see cref="OnRender"/> pass, since this project draws every chart itself rather than composing
/// one from separate controls.
/// </summary>
public sealed class StatsRingChart : FrameworkElement
{
    public StatsRingChart() => ClipToBounds = true;

    /// <summary>One slice: a label already resolved to what the legend should print (a provider or
    /// model display name, or the pooled "Other"/"Andere" entry), its percentage of the whole and its
    /// own raw token total - the ring center draws whichever slice has the largest <see cref="Total"/>
    /// by name and value, so unlike <see cref="Percent"/> (rounded for display) this needs the exact
    /// figure. <see cref="ProviderId"/> carries through only for a model slice (empty for a provider
    /// slice, which needs none, and for the pooled entry, which belongs to none) - what the window's
    /// own code-behind colors each slice by. <see cref="LegendText"/> overrides the percentage text the
    /// legend would otherwise print for this one slice (e.g. "not countable here" for a provider this
    /// machine keeps no local token count for at all) - null keeps the ordinary "12.3%" text.</summary>
    public readonly record struct Slice(string Label, double Percent, string ProviderId = "", string? LegendText = null, long Total = 0);

    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IReadOnlyList<Slice>), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Array.Empty<Slice>(), FrameworkPropertyMetadataOptions.AffectsRender, OnAccessibleNameInputChanged));

    public IReadOnlyList<Slice> Slices
    {
        get => (IReadOnlyList<Slice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <summary>Shared by every property the accessible name is built from (the slices themselves,
    /// plus <see cref="AccessibleLargestFormat"/> and the three compact-number suffixes) so a locale
    /// switch keeps the name in step, not just a data refresh.</summary>
    private static void OnAccessibleNameInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => RefreshAccessibleName((StatsRingChart)d);

    private static void RefreshAccessibleName(StatsRingChart ring)
    {
        var slices = ring.Slices;
        var list = BuildAccessibleSummary(slices);
        var largest = BuildLargestShareAccessibleText(slices, ring.AccessibleLargestFormat, ring.ThousandSuffix, ring.MillionSuffix, ring.BillionSuffix);
        var combined = largest.Length > 0 && list.Length > 0 ? $"{largest}; {list}" : largest.Length > 0 ? largest : list;
        AutomationProperties.SetName(ring, combined);
    }

    /// <summary>Pure so the summary text is unit testable without a visual tree.</summary>
    internal static string BuildAccessibleSummary(IReadOnlyList<Slice> slices) => string.Join(
        "; ", slices.Select(slice => $"{slice.Label}: {slice.LegendText ?? StatsBarChart.PercentLabel(slice.Percent, CultureInfo.CurrentCulture)}"));

    /// <summary>The index of the largest slice by <see cref="Slice.Total"/> - the same slice the ring
    /// center draws by name and value, and the same one <see cref="RefreshAccessibleName"/> announces
    /// first. A slice with no share at all (<see cref="Slice.Percent"/> zero or negative, e.g. a
    /// provider this machine tracks no local data for) is never picked; a tie keeps whichever slice
    /// came first, matching how every other tie in this window resolves. -1 when nothing qualifies
    /// (an empty list, or every slice at zero).</summary>
    internal static int LargestSliceIndex(IReadOnlyList<Slice> slices)
    {
        var bestIndex = -1;
        var bestTotal = long.MinValue;
        for (var i = 0; i < slices.Count; i++)
        {
            if (slices[i].Percent <= 0)
                continue;
            if (bestIndex < 0 || slices[i].Total > bestTotal)
            {
                bestIndex = i;
                bestTotal = slices[i].Total;
            }
        }
        return bestIndex;
    }

    /// <summary>The ring center's own two lines - pure so this is unit testable without a visual
    /// tree: the largest slice's own label, and its total in the same compact format the ring's old
    /// single center figure already used. Empty/empty when <see cref="LargestSliceIndex"/> finds
    /// nothing.</summary>
    internal static (string Name, string Value) CenterHeadline(
        IReadOnlyList<Slice> slices, string thousandSuffix, string millionSuffix, string billionSuffix)
    {
        var index = LargestSliceIndex(slices);
        if (index < 0)
            return ("", "");

        var slice = slices[index];
        return (slice.Label, StatsAggregator.ShortenTokenCountCompact(slice.Total, thousandSuffix, millionSuffix, billionSuffix));
    }

    /// <summary>"Largest share: {0}, {1}" (DE "Größter Anteil: {0}, {1}") filled in from <see
    /// cref="CenterHeadline"/> - empty when there is nothing to announce (an empty ring, or the format
    /// itself not bound to anything yet) rather than a half-filled sentence.</summary>
    internal static string BuildLargestShareAccessibleText(
        IReadOnlyList<Slice> slices, string format, string thousandSuffix, string millionSuffix, string billionSuffix)
    {
        if (string.IsNullOrEmpty(format))
            return "";

        var (name, value) = CenterHeadline(slices, thousandSuffix, millionSuffix, billionSuffix);
        return name.Length == 0 ? "" : string.Format(CultureInfo.CurrentCulture, format, name, value);
    }

    /// <summary>One brush per slice, matched by index - resolved fresh from whichever theme is
    /// active, the same code-behind convention <see cref="StatsBarChart.SeriesBrushes"/> already
    /// uses.</summary>
    public static readonly DependencyProperty SliceBrushesProperty = DependencyProperty.Register(
        nameof(SliceBrushes), typeof(IReadOnlyList<Brush>), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Array.Empty<Brush>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Brush> SliceBrushes
    {
        get => (IReadOnlyList<Brush>)GetValue(SliceBrushesProperty);
        set => SetValue(SliceBrushesProperty, value);
    }

    /// <summary>The brush both ring center lines are drawn in (the theme's normal text color).</summary>
    public static readonly DependencyProperty CenterTextBrushProperty = DependencyProperty.Register(
        nameof(CenterTextBrush), typeof(Brush), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush CenterTextBrush
    {
        get => (Brush)GetValue(CenterTextBrushProperty);
        set => SetValue(CenterTextBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsRingChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>The already-localized magnitude words the ring center's own compact value needs -
    /// the same reasoning <see cref="StatsBarChart.MillionSuffix"/> already documents for why these
    /// are plain dependency properties rather than a <c>LocalizationService</c> dependency.</summary>
    public static readonly DependencyProperty ThousandSuffixProperty = DependencyProperty.Register(
        nameof(ThousandSuffix), typeof(string), typeof(StatsRingChart),
        new FrameworkPropertyMetadata("K", FrameworkPropertyMetadataOptions.AffectsRender, OnAccessibleNameInputChanged));

    public string ThousandSuffix
    {
        get => (string)GetValue(ThousandSuffixProperty);
        set => SetValue(ThousandSuffixProperty, value);
    }

    public static readonly DependencyProperty MillionSuffixProperty = DependencyProperty.Register(
        nameof(MillionSuffix), typeof(string), typeof(StatsRingChart),
        new FrameworkPropertyMetadata("M", FrameworkPropertyMetadataOptions.AffectsRender, OnAccessibleNameInputChanged));

    public string MillionSuffix
    {
        get => (string)GetValue(MillionSuffixProperty);
        set => SetValue(MillionSuffixProperty, value);
    }

    public static readonly DependencyProperty BillionSuffixProperty = DependencyProperty.Register(
        nameof(BillionSuffix), typeof(string), typeof(StatsRingChart),
        new FrameworkPropertyMetadata("B", FrameworkPropertyMetadataOptions.AffectsRender, OnAccessibleNameInputChanged));

    public string BillionSuffix
    {
        get => (string)GetValue(BillionSuffixProperty);
        set => SetValue(BillionSuffixProperty, value);
    }

    /// <summary>"Largest share: {0}, {1}"/"Größter Anteil: {0}, {1}" - the composite format <see
    /// cref="BuildLargestShareAccessibleText"/> fills the largest slice's own name and value into, for
    /// the ring's own accessible name.</summary>
    public static readonly DependencyProperty AccessibleLargestFormatProperty = DependencyProperty.Register(
        nameof(AccessibleLargestFormat), typeof(string), typeof(StatsRingChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnAccessibleNameInputChanged));

    public string AccessibleLargestFormat
    {
        get => (string)GetValue(AccessibleLargestFormatProperty);
        set => SetValue(AccessibleLargestFormatProperty, value);
    }

    /// <summary>Every slice's own (StartAngle, SweepAngle) in degrees, running clockwise from twelve
    /// o'clock - pure and static so the geometry is unit testable without a visual tree. Its own
    /// acceptance criterion: four 25% shares produce four 90° sweeps that sum to 360°.</summary>
    internal static IReadOnlyList<(double StartAngle, double SweepAngle)> SweepAngles(IReadOnlyList<double> percents)
    {
        var result = new List<(double, double)>(percents.Count);
        var start = 0.0;
        foreach (var percent in percents)
        {
            var sweep = Math.Max(0, percent) / 100.0 * 360.0;
            result.Add((start, sweep));
            start += sweep;
        }
        return result;
    }

    /// <summary>The radius the ring's arc is drawn on and the width it is stroked with, for a ring
    /// that has to fit inside a box <paramref name="diameter"/> across. A ring is a stroked arc, so
    /// it reaches half a stroke width past its own radius: taking the radius as the full
    /// half-diameter pushed that half stroke outside the control, where ClipToBounds cut the top and
    /// the bottom off every donut. The outer edge is what the box fixes; the radius and the stroke
    /// are derived back from it, keeping the same "stroke is half the radius" proportion the ring
    /// has always been drawn with. Pure so it is unit testable without a visual tree.</summary>
    internal static (double Radius, double StrokeThickness) RingGeometry(double diameter)
    {
        var outerRadius = Math.Max(0, diameter / 2 - 2);
        var strokeThickness = Math.Max(2, outerRadius * 0.4);
        return (Math.Max(0, outerRadius - strokeThickness / 2), strokeThickness);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var slices = Slices;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = ChartFonts.UiTypeface(this);

        if (slices.Count == 0 || slices.All(slice => slice.Percent <= 0))
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                var formatted = new FormattedText(
                    EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, TextBrush, dpi);
                drawingContext.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2));
            }
            return;
        }

        const double legendWidth = 120;
        var ringArea = Math.Max(0, width - legendWidth);
        var diameter = Math.Min(ringArea, height);
        var center = new Point(ringArea / 2, height / 2);
        var (radius, strokeThickness) = RingGeometry(diameter);

        var brushes = SliceBrushes;
        var sweeps = SweepAngles(slices.Select(s => s.Percent).ToList());

        for (var i = 0; i < slices.Count; i++)
        {
            if (sweeps[i].SweepAngle <= 0)
                continue;

            var brush = i < brushes.Count ? brushes[i] : Brushes.Gray;
            var pen = new Pen(brush, strokeThickness);
            drawingContext.DrawGeometry(null, pen, ArcGeometry(center, radius, sweeps[i].StartAngle, sweeps[i].SweepAngle));
        }

        DrawCenterText(drawingContext, slices, brushes, center, radius, strokeThickness, dpi, typeface);

        DrawLegend(drawingContext, slices, brushes, ringArea, width, height, dpi, typeface);
    }

    private const double CenterValueBaseFontSize = 13;
    private const double CenterNameBaseFontSize = 10;
    private const double CenterTextMinFontSize = 7;
    // Leaves a little air inside the ring's own inner edge rather than letting the text touch the
    // stroke it sits inside.
    private const double CenterTextPadding = 6;

    /// <summary>Two centered lines naming the LARGEST slice - its own label on top (small, trimmed
    /// with an ellipsis rather than shrunk further once it no longer fits), its own total below it
    /// (bold, the same compact format the ring's old single center figure used) - sized to fit inside
    /// the ring's own hole. Both lines are centered over the ring and draw in <see cref="CenterTextBrush"/>,
    /// the normal text color of the active theme. Nothing drawn
    /// when no slice qualifies (see <see cref="LargestSliceIndex"/>) - the empty-ring case already
    /// returned out of <see cref="OnRender"/> before this is ever called with data that could reach
    /// that state.</summary>
    private void DrawCenterText(
        DrawingContext dc, IReadOnlyList<Slice> slices, IReadOnlyList<Brush> brushes,
        Point center, double radius, double strokeThickness, double dpi, Typeface typeface)
    {
        var innerDiameter = Math.Max(0, 2 * (radius - strokeThickness / 2) - CenterTextPadding * 2);
        if (innerDiameter <= 0)
            return;

        var index = LargestSliceIndex(slices);
        if (index < 0)
            return;

        var (name, value) = CenterHeadline(slices, ThousandSuffix, MillionSuffix, BillionSuffix);

        var boldTypeface = new Typeface(typeface.FontFamily, typeface.Style, FontWeights.Bold, typeface.Stretch);

        FormattedText BuildNameLine(double fontSize) => new(
            name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, CenterTextBrush, dpi)
        {
            MaxTextWidth = innerDiameter,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        FormattedText BuildValueLine(double fontSize) => new(
            value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, boldTypeface, fontSize, CenterTextBrush, dpi)
        {
            MaxTextWidth = innerDiameter,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        var nameLine = BuildNameLine(CenterNameBaseFontSize);
        var valueLine = BuildValueLine(CenterValueBaseFontSize);

        var maxLineWidth = Math.Max(nameLine.Width, valueLine.Width);
        var totalHeight = nameLine.Height + valueLine.Height;
        var scale = FitScale(maxLineWidth, totalHeight, innerDiameter, innerDiameter, CenterTextMinFontSize / CenterValueBaseFontSize);

        if (scale < 1.0)
        {
            nameLine = BuildNameLine(CenterNameBaseFontSize * scale);
            valueLine = BuildValueLine(CenterValueBaseFontSize * scale);
        }

        nameLine.TextAlignment = TextAlignment.Center;
        valueLine.TextAlignment = TextAlignment.Center;
        var blockHeight = nameLine.Height + valueLine.Height;
        var y = center.Y - blockHeight / 2;
        var left = center.X - innerDiameter / 2;
        dc.DrawText(nameLine, new Point(left, y));
        dc.DrawText(valueLine, new Point(left, y + nameLine.Height));
    }

    /// <summary>How much the two center lines must shrink to fit inside a <paramref
    /// name="availableWidth"/> by <paramref name="availableHeight"/> box - 1.0 (never enlarged) when
    /// they already fit, otherwise the smaller of the width and height ratios, floored at <paramref
    /// name="minScale"/> so the text never shrinks to the point of vanishing. Pure so the fit itself
    /// is unit testable without a visual tree.</summary>
    internal static double FitScale(double contentWidth, double contentHeight, double availableWidth, double availableHeight, double minScale)
    {
        if (availableWidth <= 0 || availableHeight <= 0)
            return minScale;
        if (contentWidth <= 0 || contentHeight <= 0)
            return 1.0;

        var scale = Math.Min(1.0, Math.Min(availableWidth / contentWidth, availableHeight / contentHeight));
        return Math.Max(scale, minScale);
    }

    /// <summary>A full ring's own degenerate case: a 360° (or, from floating-point noise past it,
    /// slightly over) sweep has an identical start and end point, which <see cref="ArcSegment"/>
    /// cannot represent as a single arc - drawn as a full ellipse stroke instead, visually the exact
    /// same ring.</summary>
    private static Geometry ArcGeometry(Point center, double radius, double startAngleDegrees, double sweepAngleDegrees)
    {
        if (sweepAngleDegrees >= 359.99)
            return new EllipseGeometry(center, radius, radius);

        var startRadians = (startAngleDegrees - 90) * Math.PI / 180;
        var endRadians = (startAngleDegrees + sweepAngleDegrees - 90) * Math.PI / 180;
        var startPoint = new Point(center.X + radius * Math.Cos(startRadians), center.Y + radius * Math.Sin(startRadians));
        var endPoint = new Point(center.X + radius * Math.Cos(endRadians), center.Y + radius * Math.Sin(endRadians));

        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            endPoint, new Size(radius, radius), 0, sweepAngleDegrees > 180, SweepDirection.Clockwise, isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void DrawLegend(
        DrawingContext dc, IReadOnlyList<Slice> slices, IReadOnlyList<Brush> brushes, double legendX, double width, double height, double dpi, Typeface typeface)
    {
        const double swatch = 10;
        const double rowHeight = 18;
        var totalHeight = slices.Count * rowHeight;
        var y = Math.Max(0, (height - totalHeight) / 2);
        var x = Math.Min(legendX + 8, Math.Max(0, width - 4));

        for (var i = 0; i < slices.Count; i++)
        {
            var brush = i < brushes.Count ? brushes[i] : Brushes.Gray;
            dc.DrawRectangle(brush, null, new Rect(x, y + (rowHeight - swatch) / 2, swatch, swatch));

            var percentText = slices[i].LegendText ?? StatsBarChart.PercentLabel(slices[i].Percent, CultureInfo.CurrentCulture);
            var text = $"{slices[i].Label} · {percentText}";
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, width - x - swatch - 8),
                // One line per row: a wrapped second line would run into the next row.
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(formatted, new Point(x + swatch + 4, y + (rowHeight - formatted.Height) / 2));

            y += rowHeight;
        }
    }
}
