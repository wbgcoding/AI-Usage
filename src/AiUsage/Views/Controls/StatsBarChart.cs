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
/// Self-drawn bar chart for the statistics window - a bare <see cref="FrameworkElement"/> with its
/// own <see cref="OnRender"/>, the same construction <see cref="HistoryChart"/> already uses for the
/// main tiles' line chart, no chart library either. Each bar can carry more than one stacked
/// segment (one per provider), drawn bottom to top in the order <see cref="SeriesBrushes"/> lists
/// them. The chart adds a labelled X axis, three Y gridlines with shortened figures, and a hover tooltip
/// naming the date and every segment's own figure - the same drawing this control already did for
/// the stacking, just no longer bare.
/// </summary>
public sealed class StatsBarChart : FrameworkElement
{
    public StatsBarChart()
    {
        ClipToBounds = true;
        IsHitTestVisible = true;
        // Arrow keys step through the bars; the focus ring is drawn by OnRender.
        Focusable = true;
        FocusVisualStyle = null;
    }

    /// <summary>One bar: a label (a day, a week, a model or a project name, depending on the
    /// window's current grouping) and its stacked segment values, already in the order <see
    /// cref="SeriesBrushes"/> colors them. <see cref="ColorProviderId"/>/<see cref="ColorRank"/> only
    /// carry meaning for an ungrouped (single-segment) bar whose own color should not follow the
    /// uniform theme accent - the model, effort and project groupings each fill them in, the view
    /// resolving them into an actual <see cref="BarBrushes"/> entry through <c>ChartPalette</c>; every
    /// other bar leaves them at their default and is colored the old way instead. <see cref="ExtraTooltipLine"/>
    /// is one more tooltip line below the total, for a bar that bundles several entries.</summary>
    public readonly record struct Bar(
        string Label, IReadOnlyList<long> StackedValues, string ColorProviderId = "", int ColorRank = 0, string? ExtraTooltipLine = null);

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<Bar>), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Array.Empty<Bar>(), FrameworkPropertyMetadataOptions.AffectsRender, OnBarsChanged));

    public IReadOnlyList<Bar> Bars
    {
        get => (IReadOnlyList<Bar>)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>Keeps the screen-reader text alternative - every chart keeps one,
    /// through AutomationProperties.Name - in step with whatever is actually drawn,
    /// without the caller having to set it by hand on every data refresh.</summary>
    private static void OnBarsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (StatsBarChart)d;
        if (chart._focusedIndex >= 0)
        {
            // The bars changed under the keyboard focus: keep it on a bar that still exists.
            var count = ((IReadOnlyList<Bar>)e.NewValue).Count;
            chart._focusedIndex = count == 0 ? -1 : Math.Min(chart._focusedIndex, count - 1);
            chart.UpdateFocusedAutomationName();
        }

        if (chart._focusedIndex < 0)
            AutomationProperties.SetName(chart, BuildAccessibleSummary((IReadOnlyList<Bar>)e.NewValue));
    }

    /// <summary>Pure so the summary text is unit testable without a visual tree.</summary>
    internal static string BuildAccessibleSummary(IReadOnlyList<Bar> bars) =>
        string.Join("; ", bars.Select(bar => $"{bar.Label}: {bar.StackedValues.Sum().ToString(CultureInfo.CurrentCulture)}"));

    public static readonly DependencyProperty SeriesBrushesProperty = DependencyProperty.Register(
        nameof(SeriesBrushes), typeof(IReadOnlyList<Brush>), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Array.Empty<Brush>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Brush> SeriesBrushes
    {
        get => (IReadOnlyList<Brush>)GetValue(SeriesBrushesProperty);
        set => SetValue(SeriesBrushesProperty, value);
    }

    /// <summary>One brush per BAR rather than per stacked segment - unlike <see
    /// cref="SeriesBrushes"/>, which names the same segment (e.g. "the provider" series) the same
    /// color across every bar. Empty (the default) keeps every bar on <see cref="SeriesBrushes"/> as
    /// before; a non-empty list takes over for a bar whose own single segment has a color of its own
    /// (a model, effort level or project), one entry per bar in <see cref="Bars"/> order, missing
    /// entries falling back to gray the same way a missing <see cref="SeriesBrushes"/> entry already
    /// does.</summary>
    public static readonly DependencyProperty BarBrushesProperty = DependencyProperty.Register(
        nameof(BarBrushes), typeof(IReadOnlyList<Brush>), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Array.Empty<Brush>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Brush> BarBrushes
    {
        get => (IReadOnlyList<Brush>)GetValue(BarBrushesProperty);
        set => SetValue(BarBrushesProperty, value);
    }

    /// <summary>One name per stacked segment (e.g. provider display names) - used only by the hover
    /// tooltip to label each segment's own figure; an empty list falls back to showing just
    /// the bar's total.</summary>
    public static readonly DependencyProperty SeriesLabelsProperty = DependencyProperty.Register(
        nameof(SeriesLabels), typeof(IReadOnlyList<string>), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<string> SeriesLabels
    {
        get => (IReadOnlyList<string>)GetValue(SeriesLabelsProperty);
        set => SetValue(SeriesLabelsProperty, value);
    }

    /// <summary>The already-localized "M"/"B" magnitude words <see
    /// cref="StatsAggregator.ShortenTokenCount"/> needs for the Y axis' shortened figures - kept as
    /// plain dependency properties rather than a <c>LocalizationService</c> dependency, the same
    /// reasoning <see cref="HistoryChart.FiveHourLabel"/> already documents.</summary>
    public static readonly DependencyProperty MillionSuffixProperty = DependencyProperty.Register(
        nameof(MillionSuffix), typeof(string), typeof(StatsBarChart),
        new FrameworkPropertyMetadata("M", FrameworkPropertyMetadataOptions.AffectsRender));

    public string MillionSuffix
    {
        get => (string)GetValue(MillionSuffixProperty);
        set => SetValue(MillionSuffixProperty, value);
    }

    public static readonly DependencyProperty BillionSuffixProperty = DependencyProperty.Register(
        nameof(BillionSuffix), typeof(string), typeof(StatsBarChart),
        new FrameworkPropertyMetadata("B", FrameworkPropertyMetadataOptions.AffectsRender));

    public string BillionSuffix
    {
        get => (string)GetValue(BillionSuffixProperty);
        set => SetValue(BillionSuffixProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>The ring drawn around the bar the keyboard focus is on - the theme's focus brush.</summary>
    public static readonly DependencyProperty FocusBrushProperty = DependencyProperty.Register(
        nameof(FocusBrush), typeof(Brush), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush FocusBrush
    {
        get => (Brush)GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(
        nameof(TooltipBackground), typeof(Brush), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TooltipBackground
    {
        get => (Brush)GetValue(TooltipBackgroundProperty);
        set => SetValue(TooltipBackgroundProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsBarChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>The unit word appended after every absolute token figure the hover tooltip prints
    /// (e.g. "tokens"/"Token") - empty by default, which leaves the tooltip exactly as it read
    /// before this property existed.</summary>
    public static readonly DependencyProperty TokenWordProperty = DependencyProperty.Register(
        nameof(TokenWord), typeof(string), typeof(StatsBarChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string TokenWord
    {
        get => (string)GetValue(TokenWordProperty);
        set => SetValue(TokenWordProperty, value);
    }

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public static readonly DependencyProperty EmptyTextBrushProperty = DependencyProperty.Register(
        nameof(EmptyTextBrush), typeof(Brush), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EmptyTextBrush
    {
        get => (Brush)GetValue(EmptyTextBrushProperty);
        set => SetValue(EmptyTextBrushProperty, value);
    }

    /// <summary>Pure and static so bar geometry is unit testable without a visual tree - given the
    /// available width/height and the bar count, every bar's x position and width.</summary>
    internal static IReadOnlyList<(double X, double Width)> Columns(double width, int barCount)
    {
        if (barCount <= 0)
            return [];

        var slot = width / barCount;
        var barWidth = Math.Max(1, slot * 0.6);
        var columns = new List<(double, double)>(barCount);
        for (var i = 0; i < barCount; i++)
            columns.Add((i * slot + (slot - barWidth) / 2, barWidth));
        return columns;
    }

    /// <summary>Pure and static so segment heights are unit testable - the top-to-bottom Y positions
    /// of every stacked segment in one bar, scaled so the tallest bar's total in the whole chart
    /// reaches <paramref name="chartHeight"/>.</summary>
    internal static IReadOnlyList<double> SegmentHeights(IReadOnlyList<long> stackedValues, double maxTotal, double chartHeight)
    {
        if (maxTotal <= 0)
            return stackedValues.Select(_ => 0.0).ToList();

        return stackedValues.Select(value => Math.Max(0, value) / maxTotal * chartHeight).ToList();
    }

    /// <summary>Which bar indices get an X axis label - evenly spaced, never more than <paramref
    /// name="maxLabels"/> of them, so a year of daily bars still reads instead of overlapping
    /// into an unreadable smear. Pure so the interval is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<int> LabelIndices(int barCount, int maxLabels)
    {
        if (barCount <= 0 || maxLabels <= 0)
            return [];

        var step = Math.Max(1, (int)Math.Ceiling(barCount / (double)maxLabels));
        var indices = new List<int>();
        for (var i = 0; i < barCount; i += step)
            indices.Add(i);
        return indices;
    }

    /// <summary>The evenly spaced label indices of <see cref="LabelIndices(int, int)"/>, with the
    /// step doubled for as long as <paramref name="widestLabel"/> plus <paramref name="gap"/> does not
    /// fit into the width one step spans (<paramref name="slotWidth"/> per bar). 24 hour bars ask for
    /// a label every 3 hours and fall back to every 6 on a narrow chart. Pure so the choice is unit
    /// testable without a visual tree.</summary>
    internal static IReadOnlyList<int> LabelIndices(int barCount, int maxLabels, double slotWidth, double widestLabel, double gap)
    {
        if (barCount <= 0 || maxLabels <= 0)
            return [];

        var step = Math.Max(1, (int)Math.Ceiling(barCount / (double)maxLabels));
        while (step < barCount && step * slotWidth < widestLabel + gap)
            step *= 2;

        var indices = new List<int>();
        for (var i = 0; i < barCount; i += step)
            indices.Add(i);
        return indices;
    }

    /// <summary>The bar indices an hour-of-day axis labels: midnight, 6, noon and 18 o'clock, whatever
    /// the width and the bar count (those at or beyond <paramref name="barCount"/> are left out).
    /// Pure so it is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<int> HourAxisLabelIndices(int barCount) =>
        HourAxisHours.Where(index => index < barCount).ToList();

    private static readonly int[] HourAxisHours = [0, 6, 12, 18];

    /// <summary>The compact axis text of one of <see cref="HourAxisLabelIndices"/> ("6a" / "6 Uhr").
    /// The tooltip keeps the full time of the bar's own label.</summary>
    internal static string HourAxisLabel(int hour) => LocalizationService.Instance["Stats.Hour.Axis" + hour.ToString(CultureInfo.InvariantCulture)];

    /// <summary>Where an axis label starts: centered under its bar, but held inside the chart so a
    /// label for one of the outermost bars is not cut off at either edge.</summary>
    internal static double ClampLabelLeft(double centeredLeft, double labelWidth, double minLeft, double chartRight) =>
        Math.Max(minLeft, Math.Min(centeredLeft, chartRight - labelWidth));

    /// <summary>A day-keyed chart with more bars than this labels its X axis per month instead of at
    /// even intervals, since a label per interval would land on arbitrary dates.</summary>
    internal const int MonthLabelMinBars = 92;

    /// <summary>The bar indices that start a month (the first of the month) when every bar is a
    /// "yyyy-MM-dd" day key and there are more than <see cref="MonthLabelMinBars"/> of them; null
    /// for any other chart, which keeps the evenly spaced <see cref="LabelIndices"/>. Pure so it is
    /// unit testable without a visual tree.</summary>
    internal static IReadOnlyList<int>? MonthLabelIndices(IReadOnlyList<Bar> bars)
    {
        if (bars.Count <= MonthLabelMinBars)
            return null;

        var indices = new List<int>();
        for (var i = 0; i < bars.Count; i++)
        {
            if (!DateOnly.TryParseExact(bars[i].Label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            if (day.Day == 1)
                indices.Add(i);
        }
        return indices;
    }

    /// <summary>The month label under a month's first bar: the abbreviated month name, with the year
    /// added on January so a chart crossing a year boundary stays readable.</summary>
    internal static string FormatMonthAxisLabel(string dayKey, CultureInfo culture)
    {
        if (!DateOnly.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return dayKey;

        var name = day.ToString("MMM", culture);
        return day.Month == 1 ? name + " " + day.Year.ToString(culture) : name;
    }

    /// <summary>A share with one decimal at most, written in the active language's percent convention.</summary>
    internal static string PercentLabel(double percent, CultureInfo culture) =>
        StatusTextMap.FormatPercent(percent.ToString("0.#", culture));

    /// <summary>The most gridlines the Y axis draws.</summary>
    private const int MaxGridLines = 4;

    /// <summary>The Y axis' gridline values: multiples of a round step (1, 2 or 5 times a power of
    /// ten, whole numbers, at least 1) from the step up to the scale maximum. The step is the
    /// smallest one that reaches <paramref name="maxTotal"/> within <see cref="MaxGridLines"/>
    /// gridlines, so the figures read 20 / 40 / 60 instead of arbitrary thirds of the exact
    /// maximum. The last tick is the scale maximum the bars are scaled to; empty for a maximum of
    /// zero. Pure so it is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<long> YAxisTicks(long maxTotal)
    {
        if (maxTotal <= 0)
            return [];

        for (var decade = 1L; ; decade *= 10)
        {
            foreach (var factor in new long[] { 1, 2, 5 })
            {
                var step = factor * decade;
                var count = maxTotal / step + (maxTotal % step > 0 ? 1 : 0);
                if (count > MaxGridLines)
                    continue;

                var ticks = new List<long>((int)count);
                for (var i = 1; i <= count; i++)
                    ticks.Add(step * i);
                return ticks;
            }
        }
    }

    /// <summary>The bar index under pixel <paramref name="x"/> of a chart area <paramref
    /// name="width"/> wide holding <paramref name="barCount"/> equal slots (the same slot width <see
    /// cref="Columns"/> itself divides the area into) - clamped to a valid index rather than -1, since
    /// every pixel column belongs to exactly one slot. Pure so hover hit testing is unit testable
    /// without a visual tree or a live mouse.</summary>
    internal static int NearestBarIndex(double x, double width, int barCount)
    {
        if (barCount <= 0 || width <= 0)
            return -1;

        var slot = width / barCount;
        return Math.Clamp((int)(x / slot), 0, barCount - 1);
    }

    private int _hoverIndex = -1;
    private ChartTooltipPopup? _tooltipPopup;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var chartWidth = Math.Max(0, ActualWidth - _leftMargin);
        var x = e.GetPosition(this).X - _leftMargin;
        var index = chartWidth > 0 ? NearestBarIndex(x, chartWidth, Bars.Count) : -1;
        var mouse = e.GetPosition(this);
        if (index == _hoverIndex)
        {
            if (index >= 0)
                _tooltipPopup?.MoveTo(mouse);
            return;
        }

        _hoverIndex = index;
        InvalidateVisual();
        UpdateTooltip(mouse);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex == -1)
            return;

        _hoverIndex = -1;
        InvalidateVisual();
        _tooltipPopup?.Hide();
    }

    /// <summary>Shows or hides the shared tooltip popup for the current <see cref="_hoverIndex"/> -
    /// the bar's own rect is recomputed the same way <see cref="OnRender"/> lays the bars out, since
    /// this control keeps no cached hit-rect list the way <see cref="StatsMonthGrid"/> does.</summary>
    private void UpdateTooltip(Point mouse) => ShowTooltipFor(_hoverIndex, mouse);

    /// <summary>The tooltip of bar <paramref name="index"/> - at the pointer when there is one, above
    /// the bar otherwise (keyboard focus).</summary>
    private void ShowTooltipFor(int index, Point? mouse)
    {
        var bars = Bars;
        if (index < 0 || index >= bars.Count || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _tooltipPopup?.Hide();
            return;
        }

        var chartWidth = Math.Max(0, ActualWidth - _leftMargin);
        var columns = Columns(chartWidth, bars.Count);
        var (barX, barWidth) = columns[index];
        var anchorRect = new Rect(barX + _leftMargin, 0, barWidth, Math.Max(0, ActualHeight - _bottomMargin));

        var lines = BuildTooltipLines(bars[index], SeriesLabels, TokenWord);
        _tooltipPopup ??= new ChartTooltipPopup();
        _tooltipPopup.Show(this, anchorRect, lines, TooltipBackground, TextBrush, AxisBrush, mouse);
    }

    private int _focusedIndex = -1;

    /// <summary>The bar the keyboard focus sits on, -1 while the chart has no focus.</summary>
    internal int FocusedIndex => _focusedIndex;

    /// <summary>Where an arrow key moves the focused bar: left and right one bar, home and end to
    /// the first and last, clamped to the bars that exist. Pure so it is testable without a visual
    /// tree.</summary>
    internal static int MoveFocusedIndex(int current, int count, Key key)
    {
        if (count <= 0)
            return -1;

        var start = Math.Clamp(current, 0, count - 1);
        return key switch
        {
            Key.Right => Math.Min(start + 1, count - 1),
            Key.Left => Math.Max(start - 1, 0),
            Key.Home => 0,
            Key.End => count - 1,
            _ => start,
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End) || Bars.Count == 0)
            return;

        e.Handled = true;
        var moved = MoveFocusedIndex(_focusedIndex, Bars.Count, e.Key);
        if (moved == _focusedIndex)
            return;

        _focusedIndex = moved;
        OnFocusedIndexChanged();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Bars.Count == 0)
            return;

        _focusedIndex = 0;
        OnFocusedIndexChanged();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_focusedIndex < 0)
            return;

        _focusedIndex = -1;
        if (_hoverIndex < 0)
            _tooltipPopup?.Hide();
        AutomationProperties.SetName(this, BuildAccessibleSummary(Bars));
        InvalidateVisual();
    }

    private void OnFocusedIndexChanged()
    {
        UpdateFocusedAutomationName();
        InvalidateVisual();
        ShowTooltipFor(_focusedIndex, null);
    }

    /// <summary>What a screen reader announces for the focused bar: the lines its tooltip shows,
    /// joined into one sentence.</summary>
    private void UpdateFocusedAutomationName()
    {
        var bars = Bars;
        if (_focusedIndex < 0 || _focusedIndex >= bars.Count)
            return;

        AutomationProperties.SetName(this, string.Join(". ", BuildTooltipLines(bars[_focusedIndex], SeriesLabels, TokenWord)));
    }

    // The left/bottom bands <see cref="OnRender"/> measures fresh every pass, from the widest Y axis
    // figure and the X axis label's own line height - never wider than the data actually needs.
    // Cached here (rather than recomputed in <see cref="OnMouseMove"/>/<see cref="UpdateTooltip"/>,
    // which have no drawing context to measure text with) so hit testing lines up with what the last
    // render pass actually drew. The two DefaultLeftMargin/DefaultBottomMargin constants are the
    // fallback for the one case nothing can be measured against: an empty chart, with no Y ticks and
    // no bars to size a band from - the same fixed width this control used everywhere before this
    // measured band existed.
    private double _leftMargin = DefaultLeftMargin;
    private double _bottomMargin = DefaultBottomMargin;
    private const double DefaultLeftMargin = 56;
    private const double DefaultBottomMargin = 32;

    // The gap between the widest Y axis figure and the axis line itself (left band), and between the
    // X axis line and its own labels below it (bottom band) - the whole point of a measured band is
    // that nothing but this gap is ever left over.
    private const double AxisLabelGap = 6;
    private const double BottomLabelGap = 4;

    /// <summary>The left axis band's own width - pure so the arithmetic is unit testable without a
    /// visual tree to measure real text against; <see cref="OnRender"/> supplies the one real,
    /// measured input (the widest Y axis figure's own rendered width).</summary>
    internal static double ComputeLeftMargin(double widestTickWidth) => Math.Max(0, widestTickWidth) + AxisLabelGap;

    /// <summary>The bottom axis band's own height - same reasoning as <see
    /// cref="ComputeLeftMargin"/>, fed the X axis label font's own single-line height.</summary>
    internal static double ComputeBottomMargin(double axisLabelHeight) => Math.Max(0, axisLabelHeight) + BottomLabelGap;

    // The topmost gridline carries the tallest bar's own figure, drawn centred on the line. Without
    // this the line sits at y=0 and the upper half of that figure is outside the control.
    private const double TopMargin = 8;

    /// <summary>The chart's own default (never more than about ten labels); the hour panels override
    /// this to 8, so their 24 columns label every three hours while the labels fit.</summary>
    public static readonly DependencyProperty MaxAxisLabelsProperty = DependencyProperty.Register(
        nameof(MaxAxisLabels), typeof(int), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsRender));

    public int MaxAxisLabels
    {
        get => (int)GetValue(MaxAxisLabelsProperty);
        set => SetValue(MaxAxisLabelsProperty, value);
    }

    /// <summary>True when the bars are the 24 hours of a day: the X axis then always labels midnight,
    /// 6, noon and 18 o'clock (<see cref="HourAxisLabelIndices"/>) instead of the evenly spaced,
    /// width-dependent labels of any other chart.</summary>
    public static readonly DependencyProperty IsHourAxisProperty = DependencyProperty.Register(
        nameof(IsHourAxis), typeof(bool), typeof(StatsBarChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsHourAxis
    {
        get => (bool)GetValue(IsHourAxisProperty);
        set => SetValue(IsHourAxisProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // The hit-test area itself - HistoryChart documents why this is needed for OnMouseMove to
        // fire at all, the same reason applies here for the hover tooltip.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var bars = Bars;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tickTypeface = new Typeface("Segoe UI");

        if (bars.Count == 0)
        {
            // Nothing to measure a tight band against - the same fixed margins this control always
            // used before this measured, content-sized band existed.
            _leftMargin = DefaultLeftMargin;
            _bottomMargin = DefaultBottomMargin;
            var emptyChartHeight = Math.Max(0, height - _bottomMargin);
            drawingContext.DrawLine(new Pen(AxisBrush, 1), new Point(_leftMargin, emptyChartHeight + 0.5), new Point(width, emptyChartHeight + 0.5));
            if (!string.IsNullOrEmpty(EmptyText))
            {
                var formatted = new FormattedText(
                    EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, EmptyTextBrush, dpi);
                drawingContext.DrawText(formatted, new Point((width - formatted.Width) / 2, (emptyChartHeight - formatted.Height) / 2));
            }
            return;
        }

        var maxTotal = bars.Select(bar => bar.StackedValues.Sum()).DefaultIfEmpty(0).Max();

        // The left and bottom margins are sized to what THIS data actually needs to print, not a
        // fixed guess wide enough for the worst case: the Y axis band is exactly the widest of the
        // three gridline figures plus AxisLabelGap, the X axis band exactly one line of axis text
        // (every label shares the same font/size, so height never varies by content) plus
        // BottomLabelGap. Measured once here, both bands stay cached in <see cref="_leftMargin"/>/
        // <see cref="_bottomMargin"/> for <see cref="OnMouseMove"/> and <see cref="UpdateTooltip"/>,
        // which lay out the exact same geometry without a second render pass to measure from.
        var yTicks = YAxisTicks(maxTotal);
        // The bars scale to the round top gridline, not the exact maximum, so the figures on the axis match the heights.
        var scaleMax = yTicks.Count > 0 ? yTicks[^1] : 0;
        var tickTexts = yTicks
            .Select(tick => new FormattedText(
                StatsAggregator.ShortenTokenCountAxis(tick, MillionSuffix, BillionSuffix), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tickTypeface, 9, TextBrush, dpi))
            .ToList();
        var widestTick = tickTexts.Select(text => text.Width).DefaultIfEmpty(0).Max();
        _leftMargin = ComputeLeftMargin(widestTick);

        var axisLabelHeight = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tickTypeface, 9, TextBrush, dpi).Height;
        _bottomMargin = ComputeBottomMargin(axisLabelHeight);

        var chartHeight = Math.Max(0, height - _bottomMargin);
        var chartWidth = Math.Max(0, width - _leftMargin);
        // What the bars and the gridlines are scaled into: the drawing area above the X axis, minus
        // the strip the topmost figure needs for its own upper half.
        var plotHeight = Math.Max(0, chartHeight - TopMargin);

        // Y axis: three gridlines plus their shortened figures (already measured above), drawn
        // behind the bars.
        for (var i = 0; i < yTicks.Count; i++)
        {
            var tick = yTicks[i];
            var tickText = tickTexts[i];
            var y = chartHeight - (plotHeight * tick / (double)scaleMax);
            drawingContext.DrawLine(new Pen(AxisBrush, 1) { DashStyle = DashStyles.Dot }, new Point(_leftMargin, y), new Point(width, y));
            // Both coordinates are held inside the control even for a figure wider or a chart shorter
            // than the margins above allow for - a clipped figure is worse than a tight one.
            drawingContext.DrawText(tickText, new Point(
                Math.Max(0, _leftMargin - AxisLabelGap - tickText.Width),
                Math.Max(0, y - tickText.Height / 2)));
        }

        drawingContext.DrawLine(new Pen(AxisBrush, 1), new Point(_leftMargin, chartHeight + 0.5), new Point(width, chartHeight + 0.5));

        var columns = Columns(chartWidth, bars.Count);
        var brushes = SeriesBrushes;
        var barBrushes = BarBrushes;

        for (var i = 0; i < bars.Count; i++)
        {
            var (barX, barWidth) = columns[i];
            var x = barX + _leftMargin;
            var segmentHeights = SegmentHeights(bars[i].StackedValues, scaleMax, plotHeight);
            var y = chartHeight;
            for (var s = 0; s < segmentHeights.Count; s++)
            {
                var segmentHeight = segmentHeights[s];
                if (segmentHeight <= 0)
                    continue;
                var brush = barBrushes.Count > 0
                    ? (i < barBrushes.Count ? barBrushes[i] : Brushes.Gray)
                    : (s < brushes.Count ? brushes[s] : Brushes.Gray);
                drawingContext.DrawRectangle(brush, null, new Rect(x, y - segmentHeight, barWidth, segmentHeight));
                y -= segmentHeight;
            }
        }

        if (IsKeyboardFocused && _focusedIndex >= 0 && _focusedIndex < bars.Count)
        {
            var (focusX, focusWidth) = columns[_focusedIndex];
            var ring = new Rect(focusX + _leftMargin - 2, 0.75, focusWidth + 4, Math.Max(0, chartHeight - 0.75));
            drawingContext.DrawRectangle(null, new Pen(FocusBrush, 1.5), ring);
        }

        // X axis: a label under every Nth bar, never more than MaxAxisLabels of them. A label pushed
        // right by the left margin, or one on a narrow chart, can reach the one before it; it is left
        // out rather than drawn over it.
        const double labelGap = 6;
        var previousRight = double.NegativeInfinity;
        var hourIndices = IsHourAxis ? HourAxisLabelIndices(bars.Count) : null;
        var monthIndices = hourIndices is null ? MonthLabelIndices(bars) : null;
        string LabelString(int index) =>
            hourIndices is not null ? HourAxisLabel(index)
            : monthIndices is null ? FormatAxisLabel(bars[index].Label, CultureInfo.CurrentCulture)
            : FormatMonthAxisLabel(bars[index].Label, CultureInfo.CurrentCulture);
        FormattedText MakeLabel(int index) => new(
            LabelString(index), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tickTypeface, 9, TextBrush, dpi);

        var labelIndices = hourIndices ?? monthIndices;
        var measured = new Dictionary<int, FormattedText>();
        if (labelIndices is null)
        {
            // The widest label decides whether the requested spacing leaves room for each one; if
            // not the step doubles (every 3 hours becomes every 6) instead of dropping labels
            // unevenly.
            var requested = LabelIndices(bars.Count, MaxAxisLabels);
            foreach (var index in requested)
                measured[index] = MakeLabel(index);
            var widest = measured.Values.Select(text => text.Width).DefaultIfEmpty(0).Max();
            labelIndices = LabelIndices(bars.Count, MaxAxisLabels, chartWidth / bars.Count, widest, labelGap);
        }

        foreach (var index in labelIndices)
        {
            var (barX, barWidth) = columns[index];
            var labelText = measured.TryGetValue(index, out var cached) ? cached : MakeLabel(index);
            var centerX = barX + _leftMargin + barWidth / 2;
            var left = ClampLabelLeft(centerX - labelText.Width / 2, labelText.Width, _leftMargin, width);
            // The four fixed hour labels are never dropped, even where they would touch.
            if (hourIndices is null && left < previousRight + labelGap)
                continue;
            drawingContext.DrawText(labelText, new Point(left, chartHeight + BottomLabelGap));
            previousRight = left + labelText.Width;
        }
    }

    /// <summary>A day-grouped bar's label is a raw "yyyy-MM-dd" key - shown as a locale-correct
    /// month and day without the year (<see cref="DateLabels.ShortMonthDay"/>) instead, since this
    /// is the one place it actually reaches the screen. A
    /// week-grouped bar's own "yyyy-Www" key becomes that week's own Monday-to-Sunday date range,
    /// compact range (see <see cref="StatsTooltipDateFormatter.FormatRange"/>).
    /// Any other label (a model or a project name, an hour, a weekday) is not that shape and passes
    /// through unchanged.</summary>
    internal static string FormatAxisLabel(string label, CultureInfo culture)
    {
        if (DateOnly.TryParseExact(label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return DateLabels.ShortMonthDay(day.ToDateTime(TimeOnly.MinValue), culture);

        if (TryParseIsoWeekLabel(label, out var start))
            return StatsTooltipDateFormatter.FormatRange(start, start.AddDays(6), culture);

        return label;
    }

    /// <summary>Parses a "yyyy-Www" ISO week key into that week's own Monday - shared by <see
    /// cref="FormatAxisLabel"/> and <see cref="BuildTooltipHeaderLine"/> so both read the exact same
    /// week from the same label.</summary>
    private static bool TryParseIsoWeekLabel(string label, out DateOnly monday)
    {
        var weekSeparator = label.IndexOf("-W", StringComparison.Ordinal);
        if (weekSeparator > 0
            && int.TryParse(label.AsSpan(0, weekSeparator), NumberStyles.None, CultureInfo.InvariantCulture, out var isoYear)
            && int.TryParse(label.AsSpan(weekSeparator + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var isoWeek))
        {
            monday = DateOnly.FromDateTime(System.Globalization.ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday));
            return true;
        }

        monday = default;
        return false;
    }

    /// <summary>The tooltip's own header line - unlike <see cref="FormatAxisLabel"/>, which keeps
    /// the X axis compact, this is where <see cref="StatsTooltipDateFormatter"/> applies: a
    /// "yyyy-MM-dd" day label gets its weekday added (a daily bucket, no time to show), a
    /// "yyyy-Www" ISO week label becomes that week's own Monday-to-Sunday date range (a weekly
    /// bucket, neither a weekday nor a time belongs to a whole week). Any other label (a model, a
    /// project name, an hour-of-day or a weekday-of-week name - none of which name one specific
    /// calendar date) passes through <see cref="FormatAxisLabel"/> unchanged.</summary>
    internal static string BuildTooltipHeaderLine(string label, CultureInfo culture)
    {
        if (DateOnly.TryParseExact(label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return StatsTooltipDateFormatter.FormatInstant(day, StatsTooltipGranularity.Day, culture);

        if (TryParseIsoWeekLabel(label, out var start))
            return StatsTooltipDateFormatter.FormatRange(start, start.AddDays(6), culture);

        return FormatAxisLabel(label, culture);
    }

    /// <summary>The hover tooltip's own text, one line per stacked segment (or a single total line
    /// when there is only one segment or no series names to label each one with) - pure so the unit
    /// suffix is testable without a visual tree. The first line (the bar's own date/label) never
    /// carries <paramref name="tokenWord"/>; every value line after it does, whenever that word is
    /// not empty.</summary>
    internal static IReadOnlyList<string> BuildTooltipLines(Bar bar, IReadOnlyList<string> seriesLabels, string tokenWord)
    {
        string WithUnit(long value)
        {
            var number = value.ToString("N0", CultureInfo.CurrentCulture);
            return string.IsNullOrEmpty(tokenWord) ? number : $"{number} {tokenWord}";
        }

        var lines = new List<string> { BuildTooltipHeaderLine(bar.Label, CultureInfo.CurrentCulture) };
        if (bar.StackedValues.Count > 1 && seriesLabels.Count > 0)
        {
            for (var s = 0; s < bar.StackedValues.Count; s++)
            {
                var name = s < seriesLabels.Count ? seriesLabels[s] : "?";
                lines.Add($"{name}: {WithUnit(bar.StackedValues[s])}");
            }
        }
        else
        {
            lines.Add(WithUnit(bar.StackedValues.Sum()));
        }

        if (!string.IsNullOrEmpty(bar.ExtraTooltipLine))
            lines.Add(bar.ExtraTooltipLine);

        return lines;
    }

}
