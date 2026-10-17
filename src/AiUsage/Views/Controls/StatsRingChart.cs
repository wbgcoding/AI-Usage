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
    /// machine keeps no local token count for at all) - null keeps the ordinary "12.3%" text.
    /// <see cref="Tooltip"/> is the hover text, one line per "\n"; null or empty shows none.
    /// <see cref="CostText"/> is the short API cost of exactly this slice's tokens ("~12,00 €"); null or
    /// empty when the estimate is off or no price is known.</summary>
    public readonly record struct Slice(
        string Label, double Percent, string ProviderId = "", string? LegendText = null, long Total = 0, string? Tooltip = null,
        string? CostText = null);

    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IReadOnlyList<Slice>), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Array.Empty<Slice>(), FrameworkPropertyMetadataOptions.AffectsRender, OnSlicesChanged));

    public IReadOnlyList<Slice> Slices
    {
        get => (IReadOnlyList<Slice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <summary>Shared by every property the accessible name is built from (the slices themselves,
    /// plus <see cref="AccessibleLargestFormat"/> and the three compact-number suffixes) so a locale
    /// switch keeps the name in step, not just a data refresh.</summary>
    private static void OnAccessibleNameInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => RefreshAccessibleName((StatsRingChart)d);

    private static void OnSlicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (StatsRingChart)d;
        ring.HideTooltip();
        RefreshAccessibleName(ring);
    }

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
        IReadOnlyList<Slice> slices, string thousandSuffix, string millionSuffix, string billionSuffix, bool wholeNumbers = false)
    {
        var index = LargestSliceIndex(slices);
        if (index < 0)
            return ("", "");

        var slice = slices[index];
        return (slice.Label, StatsAggregator.ShortenTokenCountCompact(slice.Total, thousandSuffix, millionSuffix, billionSuffix, wholeNumbers));
    }

    /// <summary>The cost line under the center figure: the cost of the same largest slice, empty when
    /// it is unknown or nothing qualifies.</summary>
    internal static string CenterCost(IReadOnlyList<Slice> slices)
    {
        var index = LargestSliceIndex(slices);
        return index < 0 ? "" : slices[index].CostText ?? "";
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

    private const double LegendGap = 12;
    private const double MinLegendWidth = 100;
    private const double LegendRowHeight = 18;
    private const double LegendBaseFontSize = 10;

    /// <summary>The ring's diameter and where the legend starts: the legend sits right after the ring
    /// and gets all the rest of the width; a narrow control shrinks the ring before the legend loses
    /// its minimum width. Pure so the layout is unit testable without a visual tree.</summary>
    internal static (double Diameter, double LegendX) RingLayout(double width, double height)
    {
        var diameter = Math.Clamp(width - MinLegendWidth - LegendGap, 0, height);
        return (diameter, diameter + LegendGap);
    }

    private const double MinRingHeight = 132;
    private const double MaxRingHeight = 240;
    private const double LegendAllowance = 150;

    // How far the texts grow with the ring: the center lines up to the ring's own growth, the legend
    // less, so it still fits beside a large ring.
    private const double MaxCenterTextScale = 1.8;
    private const double MaxLegendTextScale = 1.4;

    /// <summary>How much the center texts grow with the ring: 1 at the smallest ring, in step with the
    /// diameter above it. A fixed size left a large ring with tiny text in its middle.</summary>
    internal static double CenterTextScale(double diameter) =>
        Math.Clamp(diameter / MinRingHeight, 1.0, MaxCenterTextScale);

    /// <summary>How much the legend grows beside a ring of <paramref name="diameter"/>: like the center
    /// texts, but less, and never past what lets all <paramref name="sliceCount"/> rows fit the
    /// control's <paramref name="height"/>.</summary>
    internal static double LegendTextScale(double diameter, double height, int sliceCount)
    {
        var scale = Math.Min(MaxLegendTextScale, CenterTextScale(diameter));
        if (sliceCount > 0)
            scale = Math.Min(scale, height / (sliceCount * LegendRowHeight));
        return Math.Max(1.0, scale);
    }

    /// <summary>The control's height: the ring grows with the width the tile gives it (the legend keeps
    /// its allowance), within a fixed range. It depends on the width only, so switching the provider or
    /// the data never resizes it; only a legend with more rows than fit can push it higher.</summary>
    internal static double HeightFor(double width, int sliceCount) =>
        Math.Max(Math.Clamp(width - LegendAllowance, MinRingHeight, MaxRingHeight), sliceCount * LegendRowHeight);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, HeightFor(availableSize.Width, Slices.Count));

    /// <summary>True when a click on a slice or a legend entry means something.</summary>
    public static readonly DependencyProperty IsActivatableProperty = DependencyProperty.Register(
        nameof(IsActivatable), typeof(bool), typeof(StatsRingChart),
        new PropertyMetadata(false, (d, e) => ((StatsRingChart)d).Cursor = (bool)e.NewValue ? System.Windows.Input.Cursors.Hand : null));

    public bool IsActivatable
    {
        get => (bool)GetValue(IsActivatableProperty);
        set => SetValue(IsActivatableProperty, value);
    }

    /// <summary>Raised with the index of the slice whose arc or legend entry was clicked.</summary>
    public event EventHandler<int>? SliceActivated;

    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(
        nameof(TooltipBackground), typeof(Brush), typeof(StatsRingChart),
        new FrameworkPropertyMetadata(Brushes.Black));

    public Brush TooltipBackground
    {
        get => (Brush)GetValue(TooltipBackgroundProperty);
        set => SetValue(TooltipBackgroundProperty, value);
    }

    private ChartTooltipPopup? _tooltipPopup;
    private int _hoverIndex = -1;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var index = SliceIndexAt(point, ActualWidth, ActualHeight, Slices);
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;
        var tooltip = index >= 0 ? Slices[index].Tooltip : null;
        if (string.IsNullOrEmpty(tooltip))
        {
            _tooltipPopup?.Hide();
            return;
        }

        _tooltipPopup ??= new ChartTooltipPopup();
        _tooltipPopup.Show(this, new Rect(point, new Size(1, 1)), tooltip.Split('\n'), TooltipBackground, TextBrush, TextBrush);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideTooltip();
    }

    private void HideTooltip()
    {
        _hoverIndex = -1;
        _tooltipPopup?.Hide();
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsActivatable)
            return;

        var index = SliceIndexAt(e.GetPosition(this), ActualWidth, ActualHeight, Slices);
        if (index < 0)
            return;

        SliceActivated?.Invoke(this, index);
        e.Handled = true;
    }

    /// <summary>The slice a point lands on: its arc on the ring or its entry in the legend beside it,
    /// -1 anywhere else (the hole, the empty corners, a ring that draws nothing). Pure so the geometry
    /// is unit testable without a visual tree; it mirrors the layout <see cref="OnRender"/> draws.</summary>
    internal static int SliceIndexAt(Point point, double width, double height, IReadOnlyList<Slice> slices)
    {
        if (width <= 0 || height <= 0 || slices.Count == 0 || slices.All(slice => slice.Percent <= 0))
            return -1;

        var (diameter, legendStart) = RingLayout(width, height);
        var center = new Point(diameter / 2, height / 2);
        var (radius, strokeThickness) = RingGeometry(diameter);

        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (diameter > 0 && distance >= radius - strokeThickness / 2 && distance <= radius + strokeThickness / 2)
        {
            // Clockwise from twelve o'clock, like the arcs themselves.
            var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
            if (angle < 0)
                angle += 360;
            var sweeps = SweepAngles(slices.Select(slice => slice.Percent).ToList());
            for (var i = 0; i < slices.Count; i++)
            {
                if (sweeps[i].SweepAngle > 0 && angle >= sweeps[i].StartAngle && angle < sweeps[i].StartAngle + sweeps[i].SweepAngle)
                    return i;
            }

            return -1;
        }

        var legendX = Math.Min(legendStart, Math.Max(0, width - 4));
        var rowHeight = LegendRowHeight * LegendTextScale(diameter, height, slices.Count);
        var legendTop = Math.Max(0, (height - slices.Count * rowHeight) / 2);
        if (point.X >= legendX && point.Y >= legendTop)
        {
            var row = (int)((point.Y - legendTop) / rowHeight);
            if (row < slices.Count)
                return row;
        }

        return -1;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        // The hit-test area: without a fill, only the drawn arcs would take a click.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

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

        var (diameter, legendStart) = RingLayout(width, height);
        var center = new Point(diameter / 2, height / 2);
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

        DrawCenterText(drawingContext, slices, brushes, center, radius, strokeThickness, CenterTextScale(diameter), dpi, typeface);

        DrawLegend(drawingContext, slices, brushes, legendStart, width, height, LegendTextScale(diameter, height, slices.Count), dpi, typeface);
    }

    private const double CenterValueBaseFontSize = 13;
    private const double CenterNameBaseFontSize = 10;
    private const double CenterTextMinFontSize = 7;
    // The cost line under the figure: this share of the figure's size, never below the floor.
    private const double CenterCostFontRatio = 0.55;
    private const double CenterCostMinFontSize = 6;
    // Leaves a little air inside the ring's own inner edge rather than letting the text touch the
    // stroke it sits inside.
    private const double CenterTextPadding = 6;

    /// <summary>Two centered lines naming the LARGEST slice - its own label on top (small, trimmed
    /// with an ellipsis rather than shrunk further once it no longer fits), its own total below it
    /// (bold, the same compact format the ring's old single center figure used) - sized to fit inside
    /// the ring's own hole. Both lines are centered over the ring and draw in <see cref="CenterTextBrush"/>,
    /// the normal text color of the active theme. Nothing drawn
    /// when no slice qualifies (see <see cref="LargestSliceIndex"/>) - the empty-ring case already
    /// returns out of <see cref="OnRender"/>, so this is never called with data that could reach that
    /// state.</summary>
    private void DrawCenterText(
        DrawingContext dc, IReadOnlyList<Slice> slices, IReadOnlyList<Brush> brushes,
        Point center, double radius, double strokeThickness, double textScale, double dpi, Typeface typeface)
    {
        var nameBaseSize = CenterNameBaseFontSize * textScale;
        var valueBaseSize = CenterValueBaseFontSize * textScale;
        var innerDiameter = Math.Max(0, 2 * (radius - strokeThickness / 2) - CenterTextPadding * 2);
        if (innerDiameter <= 0)
            return;

        var index = LargestSliceIndex(slices);
        if (index < 0)
            return;

        var (name, value) = CenterHeadline(slices, ThousandSuffix, MillionSuffix, BillionSuffix);
        var costText = CenterCost(slices);

        var boldTypeface = new Typeface(typeface.FontFamily, typeface.Style, FontWeights.Bold, typeface.Stretch);

        // A hole too small for the figure even at the smallest font drops its decimal.
        var fitWidth = innerDiameter * 0.95;
        double ValueWidthAtMinimum(string text) => new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, boldTypeface, CenterTextMinFontSize, CenterTextBrush, dpi).Width;
        if (ValueWidthAtMinimum(value) > fitWidth)
            value = CenterHeadline(slices, ThousandSuffix, MillionSuffix, BillionSuffix, wholeNumbers: true).Value;

        // Measured untrimmed: a line trimmed to the hole reports the trimmed width and would always
        // look as if it fit, so the font step below would never kick in.
        FormattedText BuildNameLine(double fontSize, int maxLines, bool trim) => new(
            name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, CenterTextBrush, dpi)
        {
            MaxTextWidth = trim ? innerDiameter : 0,
            MaxLineCount = maxLines,
            Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };
        FormattedText BuildValueLine(double fontSize, bool trim) => new(
            value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, boldTypeface, fontSize, CenterTextBrush, dpi)
        {
            MaxTextWidth = trim ? innerDiameter : 0,
            MaxLineCount = 1,
            Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };

        var naturalName = BuildNameLine(nameBaseSize, 1, trim: false);
        var naturalValue = BuildValueLine(valueBaseSize, trim: false);

        // A few percent of slack: text scaled to exactly the hole's width can still be trimmed by
        // the layout's own rounding.
        const double comfortableScale = 0.85;
        var nameScale = FitScale(naturalName.Width, naturalName.Height, fitWidth, innerDiameter, CenterTextMinFontSize / nameBaseSize);
        var nameLines = 1;
        if (fitWidth / naturalName.Width < comfortableScale && name.Contains(' '))
        {
            // A long multi-word name would shrink to nothing on one line: wrap it onto a second line
            // and size it to the widest word instead.
            var widestWord = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new FormattedText(
                    word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, nameBaseSize, CenterTextBrush, dpi).Width)
                .Max();
            nameLines = 2;
            nameScale = FitScale(widestWord, naturalName.Height, fitWidth, innerDiameter, CenterTextMinFontSize / nameBaseSize);
        }

        var valueScale = FitScale(naturalValue.Width, naturalValue.Height, fitWidth, innerDiameter, CenterTextMinFontSize / valueBaseSize);
        var pairHeight = naturalName.Height * nameScale * nameLines + naturalValue.Height * valueScale;
        if (costText.Length > 0)
            pairHeight += naturalValue.Height * valueScale * CenterCostFontRatio;
        var heightScale = pairHeight > innerDiameter ? innerDiameter / pairHeight : 1.0;

        // The proportional step above is an estimate; walk the size down until the measured line
        // really fits the hole (or the minimum size is reached).
        var nameSize = Math.Max(CenterTextMinFontSize, nameBaseSize * nameScale * heightScale);
        var valueSize = Math.Max(CenterTextMinFontSize, valueBaseSize * valueScale * heightScale);
        while (valueSize > CenterTextMinFontSize && BuildValueLine(valueSize, trim: false).Width > fitWidth)
            valueSize = Math.Max(CenterTextMinFontSize, valueSize - 0.25);
        if (nameLines == 1)
        {
            while (nameSize > CenterTextMinFontSize && BuildNameLine(nameSize, 1, trim: false).Width > fitWidth)
                nameSize = Math.Max(CenterTextMinFontSize, nameSize - 0.25);
        }

        var nameLine = BuildNameLine(nameSize, nameLines, trim: true);
        var valueLine = BuildValueLine(valueSize, trim: true);

        FormattedText BuildCostLine(double fontSize, bool trim) => new(
            costText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, TextBrush, dpi)
        {
            MaxTextWidth = trim ? innerDiameter : 0,
            MaxLineCount = 1,
            Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };
        FormattedText? costLine = null;
        if (costText.Length > 0)
        {
            var costSize = Math.Max(CenterCostMinFontSize, valueSize * CenterCostFontRatio);
            while (costSize > CenterCostMinFontSize && BuildCostLine(costSize, trim: false).Width > fitWidth)
                costSize = Math.Max(CenterCostMinFontSize, costSize - 0.25);
            costLine = BuildCostLine(costSize, trim: true);
            costLine.TextAlignment = TextAlignment.Center;
        }

        nameLine.TextAlignment = TextAlignment.Center;
        valueLine.TextAlignment = TextAlignment.Center;
        var blockHeight = nameLine.Height + valueLine.Height + (costLine?.Height ?? 0);
        var y = center.Y - blockHeight / 2;
        var left = center.X - innerDiameter / 2;
        dc.DrawText(nameLine, new Point(left, y));
        dc.DrawText(valueLine, new Point(left, y + nameLine.Height));
        if (costLine is not null)
            dc.DrawText(costLine, new Point(left, y + nameLine.Height + valueLine.Height));
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
        DrawingContext dc, IReadOnlyList<Slice> slices, IReadOnlyList<Brush> brushes, double legendX, double width, double height,
        double textScale, double dpi, Typeface typeface)
    {
        var swatch = 10 * textScale;
        var rowHeight = LegendRowHeight * textScale;
        var totalHeight = slices.Count * rowHeight;
        var y = Math.Max(0, (height - totalHeight) / 2);
        var x = Math.Min(legendX, Math.Max(0, width - 4));

        for (var i = 0; i < slices.Count; i++)
        {
            var brush = i < brushes.Count ? brushes[i] : Brushes.Gray;
            dc.DrawRectangle(brush, null, new Rect(x, y + (rowHeight - swatch) / 2, swatch, swatch));

            var percentText = slices[i].LegendText ?? StatsBarChart.PercentLabel(slices[i].Percent, CultureInfo.CurrentCulture);
            var legendWidth = Math.Max(1, width - x - swatch - 8);
            var fontSize = LegendBaseFontSize * textScale;
            var figure = LegendFigure(slices[i].Label, percentText, slices[i].CostText, legendWidth, dpi, typeface, fontSize);
            var (label, percent) = LegendEntryTexts(slices[i].Label, figure, legendWidth, dpi, typeface, TextBrush, fontSize);
            var textX = x + swatch + 4 * textScale;
            dc.DrawText(label, new Point(textX, y + (rowHeight - label.Height) / 2));
            dc.DrawText(percent, new Point(textX + label.Width, y + (rowHeight - percent.Height) / 2));

            y += rowHeight;
        }
    }

    /// <summary>The legend figure of one slice: the percentage, followed by " · cost" only when the full
    /// label and the longer figure still fit <paramref name="maxWidth"/> - the cost never costs the label
    /// any of its letters.</summary>
    internal static string LegendFigure(
        string label, string percentText, string? costText, double maxWidth, double dpi, Typeface typeface, double fontSize)
    {
        if (string.IsNullOrEmpty(costText))
            return percentText;

        var longer = percentText + " \u00B7 " + costText;
        double Width(string text) => new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, dpi).Width;
        return Width(label) + Width(" \u00B7 " + longer) <= maxWidth ? longer : percentText;
    }

    /// <summary>One legend entry as two texts: the label, trimmed with an ellipsis to what is left of
    /// <paramref name="maxWidth"/>, and " · figure" right after it. The figure is the number the ring
    /// exists to show, so it is laid out first and never cut; only the label gives way.</summary>
    internal static (FormattedText Label, FormattedText Figure) LegendEntryTexts(
        string label, string figure, double maxWidth, double dpi, Typeface typeface, Brush brush, double fontSize = LegendBaseFontSize)
    {
        FormattedText Make(string text, double limit) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, brush, dpi)
            {
                MaxTextWidth = Math.Max(1, limit),
                // One line per row: a wrapped second line would run into the next row.
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

        var figureText = Make(" · " + figure, maxWidth);
        var labelText = Make(label, maxWidth - figureText.Width);
        return (labelText, figureText);
    }
}
