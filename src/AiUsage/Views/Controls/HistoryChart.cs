using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;

namespace AiUsage.Views.Controls;

/// <summary>
/// Self-drawn history chart - a bare <see cref="FrameworkElement"/> with its own
/// <see cref="OnRender"/>, no child element per data point, so drawing cost never depends on how many
/// points the caller collected. The caller pre-filters to whichever time range is selected
/// (24h/7d/30d/1 year/all; the range picker itself is a separate control) and hands over
/// plain, time-ordered percent values; this control only turns them into pixels.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    public HistoryChart()
    {
        // A value at 100% touches the top edge exactly; without this a pen's half-width can bleed
        // a pixel past the rounded border container that hosts this control.
        ClipToBounds = true;
    }

    // No hardcoded literal here on purpose: every visible string comes from LocalizationService
    // - the caller binds this to [Chart.Empty].
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(HistoryChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>One measurement at a point in time - the x axis is real elapsed time, not sample
    /// index, so a five-minute gap and an eight-hour gap between two stored points draw at
    /// visibly different widths instead of looking identical.</summary>
    public readonly record struct ChartPoint(DateTimeOffset At, double Percent);

    public static readonly DependencyProperty FiveHourValuesProperty = DependencyProperty.Register(
        nameof(FiveHourValues), typeof(IReadOnlyList<ChartPoint>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<ChartPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WeeklyValuesProperty = DependencyProperty.Register(
        nameof(WeeklyValues), typeof(IReadOnlyList<ChartPoint>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<ChartPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint> FiveHourValues
    {
        get => (IReadOnlyList<ChartPoint>)GetValue(FiveHourValuesProperty);
        set => SetValue(FiveHourValuesProperty, value);
    }

    public IReadOnlyList<ChartPoint> WeeklyValues
    {
        get => (IReadOnlyList<ChartPoint>)GetValue(WeeklyValuesProperty);
        set => SetValue(WeeklyValuesProperty, value);
    }

    /// <summary>The visible time window, handed in by the caller alongside the values themselves -
    /// without it the chart would end at the newest stored point and silently hide a gap between
    /// that point and now (a provider that stopped answering hours ago would otherwise look current).</summary>
    public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register(
        nameof(RangeStart), typeof(DateTimeOffset), typeof(HistoryChart),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register(
        nameof(RangeEnd), typeof(DateTimeOffset), typeof(HistoryChart),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTimeOffset RangeStart
    {
        get => (DateTimeOffset)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTimeOffset RangeEnd
    {
        get => (DateTimeOffset)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>Whether the range captions (and their strip under the plot) draw at all - bound to the same
    /// Full-density-only flag the surrounding tile already uses to show this control in the first
    /// place, kept as its own property rather than assumed, in case a future caller shows the chart
    /// somewhere the captions would not fit.</summary>
    public static readonly DependencyProperty ShowAxesProperty = DependencyProperty.Register(
        nameof(ShowAxes), typeof(bool), typeof(HistoryChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowAxes
    {
        get => (bool)GetValue(ShowAxesProperty);
        set => SetValue(ShowAxesProperty, value);
    }

    /// <summary>The already-localized window names, used only in the hover caption - kept as plain
    /// dependency properties rather than a lookup, so this control stays free of literal text and of
    /// any dependency on <c>LocalizationService</c> itself.</summary>
    public static readonly DependencyProperty FiveHourLabelProperty = DependencyProperty.Register(
        nameof(FiveHourLabel), typeof(string), typeof(HistoryChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WeeklyLabelProperty = DependencyProperty.Register(
        nameof(WeeklyLabel), typeof(string), typeof(HistoryChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string FiveHourLabel
    {
        get => (string)GetValue(FiveHourLabelProperty);
        set => SetValue(FiveHourLabelProperty, value);
    }

    public string WeeklyLabel
    {
        get => (string)GetValue(WeeklyLabelProperty);
        set => SetValue(WeeklyLabelProperty, value);
    }

    /// <summary>Last week's figures, already shifted forward by seven days by the caller so they
    /// draw on the same axis as the current window. Drawn last, on top of both real series (a plain
    /// muted line with no fill and no per-level color, so it still reads as background context rather
    /// than a third measurement competing with the real ones) - drawing it first, behind them, would
    /// let a real series' own opaque line paint over it and erase it completely whenever the two
    /// agreed, which for a steady user is often most of the time.</summary>
    public static readonly DependencyProperty PreviousWeekValuesProperty = DependencyProperty.Register(
        nameof(PreviousWeekValues), typeof(IReadOnlyList<ChartPoint>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<ChartPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint> PreviousWeekValues
    {
        get => (IReadOnlyList<ChartPoint>)GetValue(PreviousWeekValuesProperty);
        set => SetValue(PreviousWeekValuesProperty, value);
    }

    /// <summary>Mouse position of the current hover, relative to this control - null while the mouse
    /// is not over the chart. Read back only from <see cref="OnRender"/>, which <see
    /// cref="InvalidateVisual"/> re-runs after every move that actually changes the matched point
    /// (see <see cref="_hoverFiveHourIndex"/>/<see cref="_hoverWeeklyIndex"/>) or moves the guide line
    /// itself onto another pixel column (see <see cref="_hoverGuideX"/>) - <see cref="DrawHover"/>
    /// draws its guide line and caption box at this exact position, not at the matched data point's
    /// own pixel, so a redraw gated on the matched point alone would leave them visibly behind the
    /// cursor while it moves within one point's nearest-match range.</summary>
    private Point? _hoverPosition;

    /// <summary>The five-hour/weekly series index <see cref="DrawHover"/> matched on the previous
    /// redraw - -1 for "no match" (also the state right after <see cref="OnMouseLeave"/>).</summary>
    private int _hoverFiveHourIndex = -1;
    private int _hoverWeeklyIndex = -1;

    /// <summary>The rounded-to-the-pixel X of the previous redraw's <see cref="_hoverPosition"/> -
    /// <see cref="double.NaN"/> right after construction and after <see cref="OnMouseLeave"/>, which
    /// <see cref="HoverGuideColumnChanged"/> always reads as changed. Rounding (rather than comparing
    /// the raw sub-pixel position) is what keeps the redraw count down: many mouse-move events land
    /// inside the same visible pixel column and would otherwise all ask for a redraw nothing on
    /// screen would actually show.</summary>
    private double _hoverGuideX = double.NaN;

    /// <summary>How a series is drawn, independent of its color. The five-hour window renders as a
    /// thin dashed line with no fill so it can never be mistaken for the weekly series when both
    /// happen to sit at the same usage level; every other window kind (weekly, and any future kind a
    /// provider adds) gets the normal solid, filled look.</summary>
    public readonly record struct SeriesStyleInfo(bool IsDashed, bool HasFill);

    /// <summary>Pure and static so it is unit-testable without a visual tree.</summary>
    public static SeriesStyleInfo SeriesStyle(WindowKind kind) => kind switch
    {
        WindowKind.FiveHour => new SeriesStyleInfo(IsDashed: true, HasFill: false),
        // Weekly, and Other (any window kind a provider adds later with no dedicated design of its
        // own) both get the normal solid/filled look - solid/filled is the safe default.
        _ => new SeriesStyleInfo(IsDashed: false, HasFill: true),
    };

    /// <summary>Height of the strip under the plot that holds the range captions.</summary>
    internal const double CaptionStripHeight = 12;

    /// <summary>The smallest control height at which the caption strip is reserved at all.</summary>
    private const double CaptionMinHeight = 48;

    /// <summary>Whether the captions show at all: <see cref="ShowAxes"/> and a control tall enough
    /// that the strip leaves the plot a useful height.</summary>
    internal static bool HasCaptionStrip(double height, bool showAxes) => showAxes && height >= CaptionMinHeight;

    /// <summary>The height the curves and grid lines are mapped onto: the control's height less the
    /// caption strip while captions show, the whole height otherwise. Pure so the split is
    /// unit-testable without a visual tree.</summary>
    internal static double PlotHeight(double height, bool showAxes) =>
        HasCaptionStrip(height, showAxes) ? height - CaptionStripHeight : height;

    /// <summary>The pixel y of a percentage on a plot area of the given height (0 % at its bottom
    /// edge, 100 % at the top).</summary>
    internal static double MapY(double percent, double plotHeight) =>
        plotHeight - Math.Clamp(percent, 0, 100) / 100.0 * plotHeight;

    /// <summary>The two reference lines - 100% (top edge, solid) and 50% (dashed) - as a pure
    /// function of the plot height alone, so the geometry is unit-testable without a visual tree.</summary>
    internal static IReadOnlyList<(double Y, bool Dashed)> GridLines(double plotHeight) =>
    [
        (0.5, false),
        (Math.Round(plotHeight * 0.5) + 0.5, true),
    ];

    /// <summary>One range-end caption: local clock time for a range of a day or less, otherwise the
    /// short month and day without a year (<paramref name="withYear"/> appends it) - a duration long
    /// enough to need a date no longer fits meaningfully as a time of day alone. Pure so the
    /// threshold is unit-testable without a visual tree.</summary>
    internal static string RangeCaption(DateTimeOffset at, TimeSpan range, bool withYear = false)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = at.LocalDateTime;
        if (range <= TimeSpan.FromHours(24))
            return local.ToString("t", culture);

        var date = DateLabels.ShortMonthDay(local, culture);
        return withYear ? date + " " + local.ToString("yyyy", culture) : date;
    }

    /// <summary>Both range captions. The year shows only when the range crosses a year boundary,
    /// and then only on the start, so the end reads as the same year's continuation.</summary>
    internal static (string Start, string End) RangeCaptions(DateTimeOffset start, DateTimeOffset end)
    {
        var range = end - start;
        // Before the first history load the range is still unset; two identical captions there
        // would read as a real but empty time span.
        if (range <= TimeSpan.Zero)
            return ("", "");

        // A day-long range shows the same clock time at both ends, so the start names its day.
        if (range <= TimeSpan.FromHours(24) && start.LocalDateTime.Date != end.LocalDateTime.Date)
            return (DateLabels.ShortMonthDay(start.LocalDateTime, CultureInfo.CurrentCulture) + " " + RangeCaption(start, range),
                RangeCaption(end, range));

        var crossesYear = start.LocalDateTime.Year != end.LocalDateTime.Year;
        return (RangeCaption(start, range, withYear: crossesYear), RangeCaption(end, range));
    }

    /// <summary>The time part of a hover caption: clock time within a day, otherwise short month and
    /// day plus clock time.</summary>
    internal static string HoverTime(DateTimeOffset at, TimeSpan range)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = at.LocalDateTime;
        return range <= TimeSpan.FromHours(24)
            ? local.ToString("t", culture)
            : DateLabels.ShortMonthDay(local, culture) + " " + local.ToString("t", culture);
    }

    /// <summary>Maps an instant to its pixel x position across <see cref="RangeStart"/>..<see
    /// cref="RangeEnd"/>, clamped to the control's own bounds.</summary>
    private double MapX(DateTimeOffset at, double width)
    {
        var span = (RangeEnd - RangeStart).TotalSeconds;
        return PlotX(span > 0 ? Math.Clamp((at - RangeStart).TotalSeconds / span, 0, 1) : 0, width);
    }

    /// <summary>Half the series pen's width: the curve's first and last point sit this far inside the
    /// field, so the stroke at either edge is drawn whole instead of half of it being clipped away.</summary>
    internal const double StrokeInset = 0.75;

    /// <summary>The pixel x of a position along the visible range (0 = start, 1 = end): the curve
    /// spans the field's whole width, less only the stroke inset on each side.</summary>
    internal static double PlotX(double fraction, double width) =>
        StrokeInset + fraction * Math.Max(0, width - 2 * StrokeInset);

    /// <summary>The index of the point in <paramref name="points"/> whose pixel x position (mapped
    /// the same way <see cref="DrawSeries"/> places it) is closest to <paramref name="x"/>, or -1 when
    /// no point falls within a 12px tolerance - a pure function so the hover hit test is unit-testable
    /// without a visual tree or a live mouse.</summary>
    internal static int NearestIndex(IReadOnlyList<ChartPoint> points, double x, double width, DateTimeOffset start, DateTimeOffset end)
    {
        if (points.Count == 0)
            return -1;

        var span = (end - start).TotalSeconds;
        var bestIndex = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < points.Count; i++)
        {
            // A point outside the visible range is never drawn, so it must not be offered on hover
            // either (it would otherwise map to a pixel beyond the edges and still land within reach).
            if (points[i].At < start || points[i].At > end)
                continue;

            var px = PlotX(span > 0 ? (points[i].At - start).TotalSeconds / span : 0, width);
            var distance = Math.Abs(px - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestDistance <= 12 ? bestIndex : -1;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        _hoverPosition = position;

        var width = ActualWidth;
        var fiveHourIndex = NearestIndex(FiveHourValues ?? [], position.X, width, RangeStart, RangeEnd);
        var weeklyIndex = NearestIndex(WeeklyValues ?? [], position.X, width, RangeStart, RangeEnd);
        if (!HoverPointChanged(_hoverFiveHourIndex, _hoverWeeklyIndex, fiveHourIndex, weeklyIndex)
            && !HoverGuideColumnChanged(_hoverGuideX, position.X))
            return;

        _hoverFiveHourIndex = fiveHourIndex;
        _hoverWeeklyIndex = weeklyIndex;
        _hoverGuideX = position.X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPosition = null;
        _hoverFiveHourIndex = -1;
        _hoverWeeklyIndex = -1;
        _hoverGuideX = double.NaN;
        InvalidateVisual();
    }

    /// <summary>Whether a mouse move actually changed which data point each series would show -
    /// the pure decision <see cref="OnMouseMove"/> gates its <see cref="InvalidateVisual"/> call on,
    /// pulled out so it is unit-testable without a visual tree or a live mouse.</summary>
    internal static bool HoverPointChanged(int previousFiveHourIndex, int previousWeeklyIndex, int newFiveHourIndex, int newWeeklyIndex) =>
        newFiveHourIndex != previousFiveHourIndex || newWeeklyIndex != previousWeeklyIndex;

    /// <summary>Whether a mouse move actually moved the hover guide line/caption box (drawn at the
    /// exact cursor X, see <see cref="DrawHover"/>) onto another pixel column - the second, separate
    /// half of <see cref="OnMouseMove"/>'s redraw gate alongside <see cref="HoverPointChanged"/>,
    /// pulled out the same way for the same reason. Comparing rounded values (rather than the raw
    /// sub-pixel positions) is what keeps this from re-triggering the redraw storm <see
    /// cref="HoverPointChanged"/> was introduced to avoid.</summary>
    internal static bool HoverGuideColumnChanged(double previousGuideX, double newGuideX) =>
        Math.Round(previousGuideX) != Math.Round(newGuideX);

    /// <summary>The vertical guide line, one dot per series with a point under the pointer, and one
    /// caption box naming each matched series' value and time - the same information a screen reader
    /// gets at all times through <c>AutomationProperties.Name</c>, since hovering has no keyboard or
    /// touch equivalent.</summary>
    private void DrawHover(DrawingContext dc, double width, double plotHeight)
    {
        if (_hoverPosition is not { } hover)
            return;

        var matches = new List<(ChartPoint Point, string Label, WindowKind Kind)>();
        void TryMatch(IReadOnlyList<ChartPoint>? values, string label, WindowKind kind)
        {
            var points = values ?? [];
            var index = NearestIndex(points, hover.X, width, RangeStart, RangeEnd);
            if (index >= 0)
                matches.Add((points[index], label, kind));
        }

        TryMatch(FiveHourValues, FiveHourLabel, WindowKind.FiveHour);
        TryMatch(WeeklyValues, WeeklyLabel, WindowKind.Weekly);
        if (matches.Count == 0)
            return;

        dc.DrawLine(GetGridStyle().GuidePen, new Point(hover.X, 0), new Point(hover.X, plotHeight));

        var range = RangeEnd - RangeStart;
        var lines = new string[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            var (point, label, kind) = matches[i];
            var dotBrush = GetSeriesColours(kind, UsageRowViewModel.Classify(point.Percent)).SolidPen.Brush;
            var px = MapX(point.At, width);
            var py = MapY(point.Percent, plotHeight);
            dc.DrawEllipse(dotBrush, null, new Point(px, py), 1.5, 1.5);

            var percentText = StatusTextMap.UsagePercent(point.Percent).ToString(CultureInfo.CurrentCulture);
            var timeText = HoverTime(point.At, range);
            lines[i] = $"{label}: {StatusTextMap.FormatPercent(percentText)} · {timeText}";
        }

        DrawCaptionBox(dc, string.Join("\n", lines), hover.X, width, plotHeight);
    }

    private void DrawCaptionBox(DrawingContext dc, string text, double anchorX, double width, double height)
    {
        var fontFamily = (System.Windows.Media.FontFamily)(TryFindResource("Font.Ui") ?? new System.Windows.Media.FontFamily("Segoe UI"));
        var textBrush = (Brush)(TryFindResource("Text.Primary") ?? System.Windows.SystemColors.WindowTextBrush);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);

        const double padding = 4;
        const double gap = 8;
        var boxWidth = formatted.WidthIncludingTrailingWhitespace + padding * 2;
        var boxHeight = formatted.Height + padding * 2;

        var boxX = anchorX + gap;
        if (boxX + boxWidth > width)
            boxX = anchorX - gap - boxWidth; // flip left of the guide instead of running off the edge
        boxX = Math.Clamp(boxX, 0, Math.Max(0, width - boxWidth));
        var boxY = Math.Clamp(4, 0, Math.Max(0, height - boxHeight));

        var bgBrush = (Brush)(TryFindResource("Bg.Raised") ?? System.Windows.SystemColors.WindowBrush);
        var borderBrush = (Brush)(TryFindResource("Border") ?? System.Windows.SystemColors.GrayTextBrush);
        dc.DrawRoundedRectangle(bgBrush, new Pen(borderBrush, 1), new Rect(boxX, boxY, boxWidth, boxHeight), 4, 4);
        dc.DrawText(formatted, new Point(boxX + padding, boxY + padding));
    }

    private const double LegendPatternWidth = 12;
    private const double LegendPatternGap = 4;
    private const double LegendEntryGap = 10;
    private const double LegendClearance = 8;
    private const double CaptionInset = 4;

    /// <summary>The legend entries for the series actually drawn, five-hour first. A series whose
    /// name is empty is left out rather than shown as a bare line pattern. Pure so it is
    /// unit-testable without a visual tree.</summary>
    internal static IReadOnlyList<(WindowKind Kind, string Label)> LegendEntries(
        bool hasFiveHour, bool hasWeekly, string? fiveHourLabel, string? weeklyLabel)
    {
        var entries = new List<(WindowKind, string)>(2);
        if (hasFiveHour && !string.IsNullOrEmpty(fiveHourLabel))
            entries.Add((WindowKind.FiveHour, fiveHourLabel));
        if (hasWeekly && !string.IsNullOrEmpty(weeklyLabel))
            entries.Add((WindowKind.Weekly, weeklyLabel));
        return entries;
    }

    /// <summary>The width of a legend whose entries have the given label widths: per entry a line
    /// pattern, a small gap and the name, with a wider gap between entries.</summary>
    internal static double LegendWidth(IReadOnlyList<double> labelWidths) =>
        labelWidths.Sum(w => LegendPatternWidth + LegendPatternGap + w) + LegendEntryGap * Math.Max(0, labelWidths.Count - 1);

    /// <summary>Where the legend starts when centred in the strip, or null when it would not keep
    /// 8 px of free space on each side between the start and the end caption - a legend squeezed
    /// against a date is left out instead.</summary>
    internal static double? LegendLeft(double width, double startCaptionWidth, double endCaptionWidth, double legendWidth)
    {
        var left = (width - legendWidth) / 2;
        var earliest = CaptionInset + startCaptionWidth + LegendClearance;
        var latestEnd = width - CaptionInset - endCaptionWidth - LegendClearance;
        return left >= earliest && left + legendWidth <= latestEnd ? left : null;
    }

    /// <summary>The range's start/end captions in the strip under the plot (see <see
    /// cref="HasCaptionStrip"/>), with the legend centred between them when it fits.</summary>
    private void DrawAxisCaptions(DrawingContext dc, double width, double height, (string Label, Pen Pen)[] legend)
    {
        if (!HasCaptionStrip(height, ShowAxes))
            return;

        var plotHeight = PlotHeight(height, ShowAxes);
        var fontFamily = (System.Windows.Media.FontFamily)(TryFindResource("Font.Ui") ?? new System.Windows.Media.FontFamily("Segoe UI"));
        var textBrush = (Brush)(TryFindResource("Text.Muted") ?? System.Windows.SystemColors.GrayTextBrush);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText MakeText(string text) =>
            new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, 9, textBrush, dpi);

        double TextTop(FormattedText text) => plotHeight + (CaptionStripHeight - text.Height) / 2;

        var (startText, endText) = RangeCaptions(RangeStart, RangeEnd);
        var startCaption = MakeText(startText);
        dc.DrawText(startCaption, new Point(CaptionInset, TextTop(startCaption)));

        var endCaption = MakeText(endText);
        dc.DrawText(endCaption, new Point(width - endCaption.Width - CaptionInset, TextTop(endCaption)));

        if (legend.Length == 0)
            return;

        var labels = legend.Select(entry => MakeText(entry.Label)).ToArray();
        var legendWidth = LegendWidth(labels.Select(label => label.Width).ToArray());
        if (LegendLeft(width, startCaption.Width, endCaption.Width, legendWidth) is not { } left)
            return;

        var x = left;
        var lineY = plotHeight + CaptionStripHeight / 2;
        for (var i = 0; i < labels.Length; i++)
        {
            dc.DrawLine(legend[i].Pen, new Point(x, lineY), new Point(x + LegendPatternWidth, lineY));
            x += LegendPatternWidth + LegendPatternGap;
            dc.DrawText(labels[i], new Point(x, TextTop(labels[i])));
            x += labels[i].Width + LegendEntryGap;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // The hit-test area itself: without a rendered (even fully transparent) shape here, nothing
        // is under the pointer and OnMouseMove never fires - IsHitTestVisible alone is not enough.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var plotHeight = PlotHeight(height, ShowAxes);
        var gridStyle = GetGridStyle();
        foreach (var (y, dashed) in GridLines(plotHeight))
            drawingContext.DrawLine(dashed ? gridStyle.DashedPen : gridStyle.SolidPen, new Point(0, y), new Point(width, y));

        // Null-safe: the default only applies while the property is unset - a binding can still
        // hand over an explicit null (e.g. before the source view model finishes initialising).
        var fiveHourValues = FiveHourValues ?? [];
        var weeklyValues = WeeklyValues ?? [];
        // Decided on what will actually be drawn: points outside the range, or several points that
        // collapse into a single column, leave fewer than two and would otherwise show bare grid
        // lines instead of the empty-state text.
        var columns = Math.Max(1, (int)Math.Round(width));
        var fiveHourReduced = ReduceToColumns(fiveHourValues, columns, RangeStart, RangeEnd);
        var weeklyReduced = ReduceToColumns(weeklyValues, columns, RangeStart, RangeEnd);
        var hasFiveHour = fiveHourReduced.Count >= 2;
        var hasWeekly = weeklyReduced.Count >= 2;

        DrawAxisCaptions(drawingContext, width, height, SeriesLegend(hasFiveHour ? fiveHourReduced[^1].Percent : 0, hasFiveHour, hasWeekly));

        if (!hasFiveHour && !hasWeekly)
        {
            DrawPlaceholder(drawingContext, width, plotHeight);
            return;
        }

        // Each series is drawn from its own data and picks its own color from its own last value,
        // independently of the other series - so the five-hour dashed line can be critical-red while
        // the weekly solid line stays its own, unrelated color.
        if (hasFiveHour)
            DrawSeries(drawingContext, fiveHourReduced, width, plotHeight, WindowKind.FiveHour);
        if (hasWeekly)
            DrawSeries(drawingContext, weeklyReduced, width, plotHeight, WindowKind.Weekly);

        // Drawn last, on top of both real series: a steady user's week-over-week figure often tracks
        // its own current one closely, and a comparison line drawn UNDER an opaque real series' own
        // line would be painted over completely and vanish exactly when the two values agreed -
        // the one case a comparison is most likely to actually apply to. On top, its own opacity keeps
        // it reading as background context without ever depending on how close the real line happens
        // to run.
        DrawPreviousWeekLine(drawingContext, width, plotHeight);

        DrawHover(drawingContext, width, plotHeight);
    }

    /// <summary>The legend entries with the pen each series is drawn in; the five-hour pattern shows
    /// the colour its line has now.</summary>
    private (string Label, Pen Pen)[] SeriesLegend(double fiveHourLastPercent, bool hasFiveHour, bool hasWeekly) =>
        LegendEntries(hasFiveHour, hasWeekly, FiveHourLabel, WeeklyLabel)
            .Select(entry => (entry.Label, entry.Kind == WindowKind.FiveHour
                ? GetSeriesColours(WindowKind.FiveHour, UsageRowViewModel.Classify(fiveHourLastPercent)).DashedPen
                : GetSeriesColours(WindowKind.Weekly, UsageLevel.Ok).SolidPen))
            .ToArray();

    /// <summary>Draws one series from points already reduced to one per pixel column.</summary>
    private void DrawSeries(DrawingContext dc, IReadOnlyList<ChartPoint> reduced, double width, double plotHeight, WindowKind kind)
    {
        if (reduced.Count < 2)
            return;

        var style = SeriesStyle(kind);
        var levelStyle = GetSeriesColours(kind, UsageRowViewModel.Classify(reduced[^1].Percent));

        var points = new Point[reduced.Count];
        for (var i = 0; i < reduced.Count; i++)
        {
            var x = MapX(reduced[i].At, width);
            var y = MapY(reduced[i].Percent, plotHeight);
            points[i] = new Point(x, y);
        }

        if (style.HasFill)
        {
            var areaGeometry = new StreamGeometry();
            using (var ctx = areaGeometry.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, plotHeight), isFilled: true, isClosed: true);
                foreach (var point in points)
                    ctx.LineTo(point, isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(points[^1].X, plotHeight), isStroked: false, isSmoothJoin: false);
            }
            dc.DrawGeometry(levelStyle.Fill, null, areaGeometry);
        }

        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
        }
        dc.DrawGeometry(null, style.IsDashed ? levelStyle.DashedPen : levelStyle.SolidPen, lineGeometry);
    }

    /// <summary>The previous-week comparison line - Full density only (the same <see
    /// cref="ShowAxes"/> flag the axis captions already use), a plain 1px muted line with no fill,
    /// skipped below two points exactly like a real series would be.</summary>
    private void DrawPreviousWeekLine(DrawingContext dc, double width, double plotHeight)
    {
        if (!ShowAxes)
            return;

        var previousWeek = PreviousWeekValues ?? [];
        if (previousWeek.Count < 2)
            return;

        var columns = Math.Max(1, (int)Math.Round(width));
        var reduced = ReduceToColumns(previousWeek, columns, RangeStart, RangeEnd);
        if (reduced.Count < 2)
            return;

        var pen = GetPreviousWeekPen();

        var points = new Point[reduced.Count];
        for (var i = 0; i < reduced.Count; i++)
        {
            var x = MapX(reduced[i].At, width);
            var y = MapY(reduced[i].Percent, plotHeight);
            points[i] = new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawPlaceholder(DrawingContext dc, double width, double plotHeight)
    {
        if (string.IsNullOrEmpty(EmptyText))
            return;

        var fontFamily = (System.Windows.Media.FontFamily)(TryFindResource("Font.Ui") ?? new System.Windows.Media.FontFamily("Segoe UI"));
        var textBrush = (Brush)(TryFindResource("Text.Muted") ?? System.Windows.SystemColors.GrayTextBrush);
        var text = new FormattedText(
            EmptyText,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11,
            textBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width - 8),
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(text, new Point(4, Math.Max(0, (plotHeight - text.Height) / 2)));
    }

    private static string LevelResourceKey(UsageLevel level) => level switch
    {
        UsageLevel.Warn => "Level.Warn",
        UsageLevel.Crit => "Level.Crit",
        _ => "Level.Ok",
    };

    /// <summary>The theme resource a series is drawn in. The five-hour line keeps the colour of its
    /// usage level, so "nearly full" stays visible at a glance; the solid weekly line (and any other
    /// window kind) always uses the accent, which keeps it apart from the warning yellow.</summary>
    internal static string SeriesColourKey(WindowKind kind, UsageLevel level) =>
        kind == WindowKind.FiveHour ? LevelResourceKey(level) : "Accent";

    /// <summary>Cached, frozen draw resources for one series colour: a solid pen (weekly-style series),
    /// a dashed pen sharing the same color (five-hour-style series), and a 6%-opacity fill brush.
    /// Built once per distinct resolved theme brush and reused across renders instead of allocating a
    /// fresh Pen/Brush on every <see cref="OnRender"/> call. Rebuilt automatically the first time the
    /// theme resource resolves to a different brush instance (e.g. the user switches theme),
    /// so the cache stays correct across live theme switching instead of freezing colors forever.</summary>
    private sealed class LevelStyle
    {
        public required Brush SourceBrush { get; init; }
        public required Pen SolidPen { get; init; }
        public required Pen DashedPen { get; init; }
        public required Brush Fill { get; init; }
    }

    /// <summary>How strongly the area under a filled line is tinted: just enough to read as a surface,
    /// faint enough that it never turns muddy on a dark background.</summary>
    internal const double FillOpacity = 0.06;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LevelStyle> LevelStyleCache = new();

    private LevelStyle GetSeriesColours(WindowKind kind, UsageLevel level)
    {
        var key = SeriesColourKey(kind, level);
        var resolved = (Brush)(TryFindResource(key) ?? System.Windows.SystemColors.GrayTextBrush);

        if (LevelStyleCache.TryGetValue(key, out var cached) && ReferenceEquals(cached.SourceBrush, resolved))
            return cached;

        var solidPen = new Pen(resolved, 1.5);
        if (solidPen.CanFreeze)
            solidPen.Freeze();

        var dashedPen = new Pen(resolved, 1.5) { DashStyle = new DashStyle([3, 2], 0) };
        if (dashedPen.CanFreeze)
            dashedPen.Freeze();

        Brush fill = resolved is SolidColorBrush solid
            ? new SolidColorBrush(solid.Color) { Opacity = FillOpacity }
            : resolved;
        if (fill.CanFreeze)
            fill.Freeze();

        var style = new LevelStyle { SourceBrush = resolved, SolidPen = solidPen, DashedPen = dashedPen, Fill = fill };
        LevelStyleCache[key] = style;
        return style;
    }

    /// <summary>Cached, frozen draw resources for the grid lines and the hover guide line - a solid
    /// pen (the top edge and the hover guide, both plain "Border"), a dashed pen at half opacity (the
    /// 50% line), sharing the same "Border" theme resource <see cref="GetLevelStyle"/> already caches
    /// its own colors from. Built once per distinct resolved brush and reused across renders instead
    /// of allocating three fresh Pen/Brush instances on every <see cref="OnRender"/> and every
    /// <see cref="DrawHover"/> call.</summary>
    private sealed class GridStyle
    {
        public required Brush SourceBrush { get; init; }
        public required Pen SolidPen { get; init; }
        public required Pen DashedPen { get; init; }
        public required Pen GuidePen { get; init; }
    }

    private static GridStyle? _gridStyleCache;

    private GridStyle GetGridStyle()
    {
        var resolved = (Brush)(TryFindResource("Border") ?? System.Windows.SystemColors.GrayTextBrush);

        var cached = _gridStyleCache;
        if (cached is not null && ReferenceEquals(cached.SourceBrush, resolved))
            return cached;

        var solidPen = new Pen(resolved, 1);
        if (solidPen.CanFreeze)
            solidPen.Freeze();

        var dashedBrush = resolved is SolidColorBrush solidGrid
            ? new SolidColorBrush(solidGrid.Color) { Opacity = 0.5 }
            : resolved;
        if (dashedBrush.CanFreeze)
            dashedBrush.Freeze();
        var dashedPen = new Pen(dashedBrush, 1) { DashStyle = new DashStyle([2, 3], 0) };
        if (dashedPen.CanFreeze)
            dashedPen.Freeze();

        var guidePen = new Pen(resolved, 1);
        if (guidePen.CanFreeze)
            guidePen.Freeze();

        var style = new GridStyle { SourceBrush = resolved, SolidPen = solidPen, DashedPen = dashedPen, GuidePen = guidePen };
        _gridStyleCache = style;
        return style;
    }

    /// <summary>Cached, frozen pen for the previous-week comparison line - built once per distinct
    /// resolved "Text.Muted" brush and reused across renders instead of allocating a fresh
    /// Brush/Pen on every <see cref="DrawPreviousWeekLine"/> call.</summary>
    private static (Brush SourceBrush, Pen Pen)? _previousWeekPenCache;

    private Pen GetPreviousWeekPen()
    {
        var resolved = (Brush)(TryFindResource("Text.Muted") ?? System.Windows.SystemColors.GrayTextBrush);

        var cached = _previousWeekPenCache;
        if (cached is { } c && ReferenceEquals(c.SourceBrush, resolved))
            return c.Pen;

        var lineBrush = resolved is SolidColorBrush solidMuted
            ? new SolidColorBrush(solidMuted.Color) { Opacity = 0.8 }
            : resolved;
        if (lineBrush.CanFreeze)
            lineBrush.Freeze();

        // Same thickness as a real series' own solid line (so it is never thinned out by
        // anti-aliasing at the exact row a real series happens to occupy too) and its own dash
        // pattern, distinct from the five-hour series' dashes - between the two, the line stays
        // identifiable even where its value tracks a real series almost exactly.
        var pen = new Pen(lineBrush, 1.5) { DashStyle = new DashStyle([5, 3], 0) };
        if (pen.CanFreeze)
            pen.Freeze();

        _previousWeekPenCache = (resolved, pen);
        return pen;
    }

    /// <summary>
    /// Reduces a raw series to at most <paramref name="columns"/> points by averaging every point
    /// that falls into the same time bucket across <paramref name="start"/>..<paramref name="end"/> -
    /// a pure function so drawing cost never depends on how many points the history store handed
    /// over. Buckets by TIME, not by index: a five-minute gap and an eight-hour gap between two
    /// stored points land in buckets that many columns apart, exactly what makes the drawn line an
    /// actual time series. An empty bucket contributes nothing to the result - never the previous
    /// bucket's value repeated, which would draw a flat line where there is really no data at all.
    /// </summary>
    public static IReadOnlyList<ChartPoint> ReduceToColumns(IReadOnlyList<ChartPoint> points, int columns, DateTimeOffset start, DateTimeOffset end)
    {
        if (points.Count == 0 || columns <= 0)
            return [];

        var span = end - start;
        if (span <= TimeSpan.Zero)
            return [];

        var sums = new double[columns];
        var counts = new int[columns];
        var lastAt = new DateTimeOffset[columns];

        foreach (var point in points)
        {
            var fraction = (point.At - start) / span;
            var bucket = (int)(fraction * columns);
            if (bucket < 0 || bucket >= columns)
                continue; // outside the requested range

            sums[bucket] += point.Percent;
            counts[bucket]++;
            lastAt[bucket] = point.At; // representative timestamp for the bucket
        }

        var result = new List<ChartPoint>(columns);
        for (var c = 0; c < columns; c++)
        {
            if (counts[c] == 0)
                continue;
            result.Add(new ChartPoint(lastAt[c], sums[c] / counts[c]));
        }

        return result;
    }
}
