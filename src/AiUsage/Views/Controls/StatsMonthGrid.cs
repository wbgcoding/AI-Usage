using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using AiUsage.Stats;

namespace AiUsage.Views.Controls;

/// <summary>
/// Self-drawn contribution-graph heatmap for the statistics window's "Verbrauch je Tag" section,
/// the same bare <see cref="FrameworkElement"/>/<see cref="OnRender"/> construction <see
/// cref="StatsBarChart"/> already uses, no chart library. One continuous grid covering the whole
/// last twelve months: columns are calendar weeks (oldest left, today right), seven rows are
/// weekdays starting at the current culture's first day of week, and every day in the span draws
/// its own square - including a day with no usage at all, filled in a neutral surface tone rather
/// than left out, and a day still ahead of today, drawn the same way at reduced opacity and never
/// clickable. Cell color is the hue of whichever provider led that day (<see
/// cref="ChartPalette.ForProvider"/>), stepped by intensity, so the grid also shows at a glance
/// which provider carried each part of the period.
/// </summary>
public sealed class StatsMonthGrid : FrameworkElement
{
    public StatsMonthGrid()
    {
        ClipToBounds = true;
        IsHitTestVisible = true;
        // Keyboard users get the same day picking as the mouse; the focus ring is drawn by OnRender.
        Focusable = true;
        FocusVisualStyle = null;
    }

    /// <summary>One provider's own total for a single day - the hover tooltip's per-provider
    /// breakdown line, largest first (the same order <see cref="ByProvider"/> is always built in).</summary>
    public readonly record struct ProviderTotal(string ProviderId, long Total);

    /// <summary>One day's own total, the provider that led it (the largest single share, empty for
    /// a day with no usage at all) and every provider that contributed to it, largest first. <see
    /// cref="Total"/> already sums every provider, not just the leader's own share; <see
    /// cref="ByProvider"/> is what the hover tooltip lists to show that sum broken down.
    /// <see cref="LeaderProviderId"/> and <see cref="ByProvider"/> both default so the handful of
    /// existing call sites in tests that construct a bare total keep compiling.</summary>
    public readonly record struct DayValue(DateOnly Day, long Total, string LeaderProviderId = "", IReadOnlyList<ProviderTotal>? ByProvider = null);

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayValue>), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Array.Empty<DayValue>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnSummaryInputChanged));

    public IReadOnlyList<DayValue> Days
    {
        get => (IReadOnlyList<DayValue>)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>The sorted non-zero day totals the color steps are bucketed against. A caller that
    /// shows a slice of a longer history hands in the whole history's totals here, so resizing
    /// (which moves the slice) never shifts a day's shade; null derives them from the drawn span.</summary>
    public static readonly DependencyProperty ColorScaleTotalsProperty = DependencyProperty.Register(
        nameof(ColorScaleTotals), typeof(IReadOnlyList<long>), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<long>? ColorScaleTotals
    {
        get => (IReadOnlyList<long>?)GetValue(ColorScaleTotalsProperty);
        set => SetValue(ColorScaleTotalsProperty, value);
    }

    /// <summary>The scale a render buckets against: the caller's whole-history one, else the drawn span's.</summary>
    internal static IReadOnlyList<long> ResolveColorScale(
        IReadOnlyList<long>? explicitScale, IReadOnlyList<DayValue> days, DateOnly rangeStart, DateOnly rangeEnd) =>
        explicitScale ?? SortedNonZeroTotals(days, rangeStart, rangeEnd);

    private static void OnSummaryInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (StatsMonthGrid)d;
        if (grid.IsKeyboardFocused && grid._focusedDay is not null)
            grid.UpdateFocusedAutomationName();
        else
            AutomationProperties.SetName(grid, BuildAccessibleSummary(grid.Days, grid.RangeStart, grid.RangeEnd, CultureInfo.CurrentCulture));
    }

    /// <summary>Pure so the summary text is unit testable without a visual tree - the whole covered
    /// range and its total, not a line per day (365 lines would be unusable for a screen reader
    /// anyway).</summary>
    internal static string BuildAccessibleSummary(IReadOnlyList<DayValue> days, DateOnly rangeStart, DateOnly rangeEnd, CultureInfo culture)
    {
        if (rangeEnd < rangeStart)
            return "";

        // Only the shown span: a caller may hand in more days than it draws (the widget tile keeps a
        // year of weeks loaded and shows as many as fit).
        var total = days.Where(day => day.Day >= rangeStart && day.Day <= rangeEnd).Sum(day => day.Total);
        return $"{StatsTooltipDateFormatter.FormatRange(rangeStart, rangeEnd, culture)}: {total.ToString("N0", culture)}";
    }

    /// <summary>The grid's own span, set by the caller and never clamped or extended here: it
    /// covers the same stretch regardless of how much of it has usage yet.</summary>
    public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register(
        nameof(RangeStart), typeof(DateOnly), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(default(DateOnly), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnSummaryInputChanged));

    public DateOnly RangeStart
    {
        get => (DateOnly)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register(
        nameof(RangeEnd), typeof(DateOnly), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(default(DateOnly), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnSummaryInputChanged));

    public DateOnly RangeEnd
    {
        get => (DateOnly)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>The first day of the period the statistics window currently reports on, null for
    /// no emphasis at all (the whole history, or a period that covers the entire grid): cells
    /// outside <see cref="HighlightStart"/> to <see cref="HighlightEnd"/> are drawn faded but stay
    /// clickable.</summary>
    public static readonly DependencyProperty HighlightStartProperty = DependencyProperty.Register(
        nameof(HighlightStart), typeof(DateOnly?), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateOnly? HighlightStart
    {
        get => (DateOnly?)GetValue(HighlightStartProperty);
        set => SetValue(HighlightStartProperty, value);
    }

    public static readonly DependencyProperty HighlightEndProperty = DependencyProperty.Register(
        nameof(HighlightEnd), typeof(DateOnly?), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateOnly? HighlightEnd
    {
        get => (DateOnly?)GetValue(HighlightEndProperty);
        set => SetValue(HighlightEndProperty, value);
    }

    /// <summary>Today's own date, set by the caller rather than read from the clock in here so the
    /// geometry stays pure and testable - decides which of the year's cells are still in the
    /// future (drawn faded, never clickable).</summary>
    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateOnly), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(default(DateOnly), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public DateOnly Today
    {
        get => (DateOnly)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    /// <summary>The "no usage" square fill - a neutral surface/border tone distinct from the
    /// surrounding card's own background, so an empty day still reads as a drawn cell rather than a
    /// hole in the grid. A future day reuses this same brush at reduced opacity.</summary>
    public static readonly DependencyProperty EmptyCellBrushProperty = DependencyProperty.Register(
        nameof(EmptyCellBrush), typeof(Brush), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EmptyCellBrush
    {
        get => (Brush)GetValue(EmptyCellBrushProperty);
        set => SetValue(EmptyCellBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty EmptyTextBrushProperty = DependencyProperty.Register(
        nameof(EmptyTextBrush), typeof(Brush), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EmptyTextBrush
    {
        get => (Brush)GetValue(EmptyTextBrushProperty);
        set => SetValue(EmptyTextBrushProperty, value);
    }

    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(
        nameof(TooltipBackground), typeof(Brush), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TooltipBackground
    {
        get => (Brush)GetValue(TooltipBackgroundProperty);
        set => SetValue(TooltipBackgroundProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>The same unit word <see cref="StatsBarChart.TokenWord"/> already appends to its own
    /// hover tooltip's value line.</summary>
    public static readonly DependencyProperty TokenWordProperty = DependencyProperty.Register(
        nameof(TokenWord), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string TokenWord
    {
        get => (string)GetValue(TokenWordProperty);
        set => SetValue(TokenWordProperty, value);
    }

    /// <summary>The tooltip's own "total" row label (e.g. "Gesamt"/"Total") - never resolved by this
    /// control itself, the same reasoning <see cref="TokenWord"/> already carries.</summary>
    public static readonly DependencyProperty TotalLabelProperty = DependencyProperty.Register(
        nameof(TotalLabel), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string TotalLabel
    {
        get => (string)GetValue(TotalLabelProperty);
        set => SetValue(TotalLabelProperty, value);
    }

    /// <summary>The empty day's own tooltip line (e.g. "keine Nutzung"/"no usage") - shown in place of
    /// a provider breakdown and total, since neither exists for that day.</summary>
    public static readonly DependencyProperty NoUsageTextProperty = DependencyProperty.Register(
        nameof(NoUsageText), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string NoUsageText
    {
        get => (string)GetValue(NoUsageTextProperty);
        set => SetValue(NoUsageTextProperty, value);
    }

    public static readonly DependencyProperty LegendLessTextProperty = DependencyProperty.Register(
        nameof(LegendLessText), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string LegendLessText
    {
        get => (string)GetValue(LegendLessTextProperty);
        set => SetValue(LegendLessTextProperty, value);
    }

    public static readonly DependencyProperty LegendMoreTextProperty = DependencyProperty.Register(
        nameof(LegendMoreText), typeof(string), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string LegendMoreText
    {
        get => (string)GetValue(LegendMoreTextProperty);
        set => SetValue(LegendMoreTextProperty, value);
    }

    /// <summary>True (the default, unchanged from before this existed - the statistics window never
    /// sets it) draws the color-key/intensity-scale row below the grid; false skips it and its own
    /// height entirely, for a caller with too little room to spare - the widget's own day-grid tile
    /// at Mini density (see <see cref="ViewModels.DayGridTileViewModel.ShowLegend"/>).</summary>
    public static readonly DependencyProperty ShowLegendProperty = DependencyProperty.Register(
        nameof(ShowLegend), typeof(bool), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool ShowLegend
    {
        get => (bool)GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }

    /// <summary>True (the default) draws the weekday column and the month row; false skips both -
    /// the widget's day-grid tile at its narrowest (Mini) density, where neither fits.</summary>
    public static readonly DependencyProperty ShowAxisLabelsProperty = DependencyProperty.Register(
        nameof(ShowAxisLabels), typeof(bool), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool ShowAxisLabels
    {
        get => (bool)GetValue(ShowAxisLabelsProperty);
        set => SetValue(ShowAxisLabelsProperty, value);
    }

    /// <summary>Lays the range out as one horizontal row of days, oldest on the left, instead of
    /// week-columns - the widget's small tile, which shows as many recent days as its width fits.</summary>
    public static readonly DependencyProperty SingleRowProperty = DependencyProperty.Register(
        nameof(SingleRow), typeof(bool), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool SingleRow
    {
        get => (bool)GetValue(SingleRowProperty);
        set => SetValue(SingleRowProperty, value);
    }

    /// <summary>The day currently outlined in <see cref="AccentBrush"/> - set by the caller from
    /// whichever day <see cref="DaySelected"/> last reported (this control never tracks its own
    /// selection state), null for no outline at all.</summary>
    public static readonly DependencyProperty SelectedDayProperty = DependencyProperty.Register(
        nameof(SelectedDay), typeof(DateOnly?), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateOnly? SelectedDay
    {
        get => (DateOnly?)GetValue(SelectedDayProperty);
        set => SetValue(SelectedDayProperty, value);
    }

    /// <summary>The selected-day outline's own color - drawn as a halo just outside the cell.</summary>
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>The width this control lays its year out against - fed by the caller from the
    /// hosting <c>ScrollViewer</c>'s own viewport width (that ScrollViewer measures its content
    /// with infinite width, so the control cannot read a usable constraint off
    /// <c>MeasureOverride</c>'s own <see cref="Size"/> parameter the way a plain child could).
    /// Cells shrink to fit this width down to <see cref="MinCellSize"/>; past that point the
    /// control reports its own real (wider) desired size instead of clipping, and the ScrollViewer
    /// scrolls to it.</summary>
    public static readonly DependencyProperty AvailableWidthProperty = DependencyProperty.Register(
        nameof(AvailableWidth), typeof(double), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(400.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double AvailableWidth
    {
        get => (double)GetValue(AvailableWidthProperty);
        set => SetValue(AvailableWidthProperty, value);
    }

    /// <summary>Fired on a click on a real, non-future day square - the caller decides what a
    /// repeat click on the same day means (this window's own view model toggles the selection off
    /// again), this control only ever reports the click itself.</summary>
    public event EventHandler<DateOnly>? DaySelected;

    // Declared before the property that uses it as its default: static initializers run in
    // declaration order, and a later declaration would leave the default null.
    private static readonly IReadOnlyDictionary<string, string> EmptyDisplayNames = new Dictionary<string, string>();

    public static readonly DependencyProperty ProviderDisplayNamesProperty = DependencyProperty.Register(
        nameof(ProviderDisplayNames), typeof(IReadOnlyDictionary<string, string>), typeof(StatsMonthGrid),
        new FrameworkPropertyMetadata(EmptyDisplayNames, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Provider id to display name, for the hover tooltip's per-provider breakdown and the
    /// color-key legend - the control never resolves a display name itself (every other piece of
    /// shown text in this app is resource-driven from the caller, never looked up here), so an id
    /// this map does not carry falls back to the raw id unchanged.</summary>
    public IReadOnlyDictionary<string, string> ProviderDisplayNames
    {
        get => (IReadOnlyDictionary<string, string>)GetValue(ProviderDisplayNamesProperty);
        set => SetValue(ProviderDisplayNamesProperty, value);
    }

    // The spacing grid this app draws everything else on (0/4/8/12/16/24) is too coarse for a
    // calendar square this small - these constants are this control's own fixed geometry, the same
    // reasoning StatsBarChart's margins already document.
    internal const double DefaultCellSize = 11;
    internal const double MinCellSize = 8;
    private const double CellGap = 3;
    private const double CellCorner = 2;
    private const double OutsideHighlightOpacity = 0.35;
    private const double LeftLabelWidth = 22;

    /// <summary>How many week columns fit <paramref name="availableWidth"/> at <see
    /// cref="DefaultCellSize"/> next to the weekday column, never less than 1 - what the widget tile
    /// and the statistics window use to decide how far back the grid reaches.</summary>
    internal static int ColumnsFitting(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth < 0)
            availableWidth = 0;
        // Far past any real screen; keeps the int cast and the date math safe for a bogus width.
        availableWidth = Math.Min(availableWidth, 1_000_000);
        var usable = Math.Max(0, availableWidth - LeftLabelWidth);
        return Math.Max(1, (int)Math.Floor((usable + CellGap) / (DefaultCellSize + CellGap)));
    }
    /// <summary>How many day cells fit <paramref name="availableWidth"/> in one row at <see
    /// cref="DefaultCellSize"/> with no weekday column (the single-row strip), never less than 1.</summary>
    internal static int StripCellsFitting(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth < 0)
            availableWidth = 0;
        availableWidth = Math.Min(availableWidth, 1_000_000);
        return Math.Max(1, (int)Math.Floor((availableWidth + CellGap) / (DefaultCellSize + CellGap)));
    }

    private const double TopLabelHeight = 14;
    private const double LegendGapTop = 10;
    private const double LegendRowHeight = 20;
    private const double EmptyHeight = 60;

    /// <summary>How many week-columns a grid starting at <paramref name="gridStart"/> (already a week
    /// start) needs to reach <paramref name="rangeEnd"/> - zero for an inverted range.</summary>
    internal static int TotalColumns(DateOnly gridStart, DateOnly rangeEnd)
    {
        if (rangeEnd < gridStart)
            return 0;
        return (int)Math.Ceiling((rangeEnd.DayNumber - gridStart.DayNumber + 1) / 7.0);
    }

    /// <summary>One calendar day's own cell, or null for a slot past <c>rangeEnd</c> (padding the
    /// last week-column out to a full week, never a real day of the year). <see cref="IsFuture"/>
    /// marks a real day of the year that has not happened yet - drawn, but faded and never
    /// clickable, unlike a null slot which is not drawn at all. <see cref="IsOutsideHighlight"/>
    /// marks a past day outside the emphasised period: drawn faded, still clickable.</summary>
    internal readonly record struct GridCell(
        DateOnly Day, long Total, string LeaderProviderId, IReadOnlyList<ProviderTotal> ByProvider, bool IsFuture = false,
        bool IsOutsideHighlight = false);

    /// <summary>Builds every week-column from <paramref name="gridStart"/> through the week
    /// containing <paramref name="rangeEnd"/>, seven cells each - each entry here is one column's
    /// own seven rows, index 0 the first weekday. A day with no matching entry in <paramref
    /// name="days"/> gets a zero total and an empty breakdown - the same "no usage" cell an explicit
    /// zero-total entry would draw. A day after <paramref name="today"/> is still built (unlike a
    /// day after <paramref name="rangeEnd"/>, which is left null) but flagged <see
    /// cref="GridCell.IsFuture"/> so it draws faded and ignores its own record data, if any -
    /// nothing in the grid's own span should ever have usage ahead of today anyway. A real day
    /// before <paramref name="highlightStart"/> or after <paramref name="highlightEnd"/> (each
    /// ignored while null) is flagged <see cref="GridCell.IsOutsideHighlight"/>.</summary>
    internal static IReadOnlyList<GridCell?[]> BuildColumns(
        DateOnly gridStart, DateOnly rangeEnd, DateOnly today, IReadOnlyList<DayValue> days, int totalColumns,
        DateOnly? highlightStart = null, DateOnly? highlightEnd = null)
    {
        var byDay = new Dictionary<DateOnly, DayValue>();
        foreach (var day in days)
            byDay[day.Day] = day;

        var columns = new List<GridCell?[]>(Math.Max(0, totalColumns));
        for (var column = 0; column < totalColumns; column++)
        {
            var weekStart = gridStart.AddDays(column * 7);
            var cells = new GridCell?[7];
            for (var row = 0; row < 7; row++)
            {
                var day = weekStart.AddDays(row);
                if (day > rangeEnd)
                    continue;

                if (day > today)
                {
                    cells[row] = new GridCell(day, 0, "", [], IsFuture: true);
                    continue;
                }

                var value = byDay.TryGetValue(day, out var found) ? found : new DayValue(day, 0);
                var outside = (highlightStart is { } start && day < start) || (highlightEnd is { } end && day > end);
                cells[row] = new GridCell(day, value.Total, value.LeaderProviderId, value.ByProvider ?? [], IsOutsideHighlight: outside);
            }
            columns.Add(cells);
        }
        return columns;
    }

    /// <summary>The single-row layout: one column per day from <paramref name="rangeStart"/> to
    /// <paramref name="rangeEnd"/>, each holding only its first slot, so the drawing and hit-testing
    /// code stays the same as for week-columns.</summary>
    internal static IReadOnlyList<GridCell?[]> BuildStrip(
        DateOnly rangeStart, DateOnly rangeEnd, DateOnly today, IReadOnlyList<DayValue> days)
    {
        var columns = new List<GridCell?[]>();
        for (var day = rangeStart; day <= rangeEnd; day = day.AddDays(1))
        {
            var week = BuildColumns(day, day, today, days, 1)[0];
            columns.Add([week[0], null, null, null, null, null, null]);
        }
        return columns;
    }

    /// <summary>Every non-zero day's own total, ascending - the quantile boundaries <see
    /// cref="QuantileStep"/> buckets a value against.</summary>
    internal static IReadOnlyList<long> SortedNonZeroTotals(IReadOnlyList<DayValue> days) =>
        days.Where(day => day.Total > 0).Select(day => day.Total).OrderBy(total => total).ToList();

    /// <summary>The same boundaries over the drawn span only: a caller may hand in more days than it
    /// draws, and days outside the span must not move the shade of a day inside it.</summary>
    internal static IReadOnlyList<long> SortedNonZeroTotals(IReadOnlyList<DayValue> days, DateOnly rangeStart, DateOnly rangeEnd) =>
        SortedNonZeroTotals([.. days.Where(day => day.Day >= rangeStart && day.Day <= rangeEnd)]);

    /// <summary>A day's own fill step: 0 for no usage at all, otherwise 1..4 by which quarter of
    /// <paramref name="sortedNonZeroTotals"/> it falls into - a handful of days at the top of a
    /// heavily skewed distribution no longer wash out every other day into "empty", the way scaling
    /// against the single busiest day used to.</summary>
    internal static int QuantileStep(long value, IReadOnlyList<long> sortedNonZeroTotals)
    {
        if (value <= 0 || sortedNonZeroTotals.Count == 0)
            return 0;

        var count = sortedNonZeroTotals.Count;
        long Percentile(double p) => sortedNonZeroTotals[Math.Clamp((int)Math.Ceiling(p * count) - 1, 0, count - 1)];

        if (value <= Percentile(0.25)) return 1;
        if (value <= Percentile(0.5)) return 2;
        if (value <= Percentile(0.75)) return 3;
        return 4;
    }

    /// <summary>One provider that led at least one day of the year, and the sum of every day it
    /// led - the color-key legend's own rows, largest first. Not that provider's whole share of the
    /// year (only its leading days count here), the one figure this pure function can compute from
    /// a day list alone.</summary>
    internal readonly record struct LegendEntry(string ProviderId, long Total);

    /// <summary>Every provider that shows up as a leader anywhere in <paramref name="days"/>,
    /// largest total first - the color key drawn beside the less/more intensity scale so a filled
    /// cell's own hue can be told apart from a neighbor led by a different provider.</summary>
    internal static IReadOnlyList<LegendEntry> BuildProviderLegend(IReadOnlyList<DayValue> days) =>
        days
            .Where(day => day.Total > 0 && !string.IsNullOrEmpty(day.LeaderProviderId))
            .GroupBy(day => day.LeaderProviderId)
            .Select(group => new LegendEntry(group.Key, group.Sum(day => day.Total)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

    /// <summary>The fewest columns two month labels may sit apart - close enough (the grid's very
    /// first, often-partial month next to a month starting only a week or two later) and the two
    /// texts would overlap at this cell size, so the later one is skipped entirely rather than drawn
    /// on top of the first.</summary>
    private const int MinColumnsBetweenMonthLabels = 3;

    /// <summary>Where every month's own label lands: the column holding that month's first day, or -
    /// failing that, since the grid's own leftmost column rarely starts on the 1st - the grid's very
    /// first column, labeled with whatever month its own first real cell falls in. Never two labels
    /// for the same month twice in a row, and never two labels closer than <see
    /// cref="MinColumnsBetweenMonthLabels"/> columns apart. Once the grid spans more than a year (a
    /// wide window), January carries the two-digit year ("Jan 26") so repeated month names stay apart.</summary>
    internal static IReadOnlyList<(int Column, string Label)> MonthLabels(IReadOnlyList<GridCell?[]> columns, CultureInfo culture)
    {
        var labels = new List<(int, string)>();
        var cells = columns.SelectMany(column => column).OfType<GridCell>().ToList();
        var spansMoreThanAYear = cells.Count > 0
            && cells.Max(cell => cell.Day).DayNumber - cells.Min(cell => cell.Day).DayNumber > 366;
        (int Year, int Month)? lastMonth = null;
        int? lastLabelColumn = null;
        for (var column = 0; column < columns.Count; column++)
        {
            var labelCell = columns[column].FirstOrDefault(cell => cell is { Day.Day: 1 });
            if (labelCell is null && column == 0)
                labelCell = columns[column].FirstOrDefault(cell => cell is not null);
            if (labelCell is not { } cell)
                continue;

            var monthKey = (cell.Day.Year, cell.Day.Month);
            if (monthKey == lastMonth || (lastLabelColumn is { } last && column - last < MinColumnsBetweenMonthLabels))
                continue;

            var format = spansMoreThanAYear && cell.Day.Month == 1 ? "MMM yy" : "MMM";
            labels.Add((column, cell.Day.ToDateTime(TimeOnly.MinValue).ToString(format, culture)));
            lastMonth = monthKey;
            lastLabelColumn = column;
        }
        return labels;
    }

    internal readonly record struct GridFit(double CellSize, int VisibleColumns);

    /// <summary>How the grid fits <paramref name="totalColumns"/> weeks into <paramref
    /// name="availableWidth"/>: the default cell size while every column still fits, otherwise a
    /// shrunk size down to <see cref="MinCellSize"/> - every column always stays, none is ever
    /// dropped, so a width too small even for the minimum size still gets every column drawn at
    /// that minimum, wider than the available space. The caller hosts this control inside a
    /// horizontally scrolling container for exactly that case, rather than clipping the overflow.</summary>
    internal static GridFit FitColumns(int totalColumns, double availableWidth, double gap, double defaultSize, double minSize)
    {
        if (totalColumns <= 0)
            return new GridFit(defaultSize, totalColumns);

        double Width(int columns, double size) => columns * size + Math.Max(0, columns - 1) * gap;

        if (availableWidth > 0 && Width(totalColumns, defaultSize) <= availableWidth)
            return new GridFit(defaultSize, totalColumns);
        if (availableWidth <= 0)
            return new GridFit(minSize, totalColumns);

        var shrunk = (availableWidth - Math.Max(0, totalColumns - 1) * gap) / totalColumns;
        return new GridFit(Math.Max(shrunk, minSize), totalColumns);
    }

    private static Color WithIntensity(Color color, int step) =>
        Color.FromArgb((byte)Math.Clamp(step * 255 / 4, 0, 255), color.R, color.G, color.B);

    // A few provider colors times four steps: every render reuses these instead of allocating a
    // brush per cell. Frozen, so they are shareable and WPF skips change tracking for them.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Color Color, int Step), SolidColorBrush> IntensityBrushes = new();

    internal static SolidColorBrush IntensityBrush(Color color, int step) =>
        IntensityBrushes.GetOrAdd((color, step), key =>
        {
            var brush = new SolidColorBrush(WithIntensity(key.Color, key.Step));
            brush.Freeze();
            return brush;
        });

    private static Color BrushColor(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    /// <summary>The weekday column's own width - zero while <see cref="ShowAxisLabels"/> is off, so
    /// the grid's cells start flush at the left edge instead of leaving a blank gutter for a column
    /// that is never drawn.</summary>
    private double EffectiveLeftLabelWidth => ShowAxisLabels ? LeftLabelWidth : 0;

    /// <summary>Same reasoning as <see cref="EffectiveLeftLabelWidth"/>, for the month row above the
    /// grid.</summary>
    private double EffectiveTopLabelHeight => ShowAxisLabels ? TopLabelHeight : 0;

    private double ContentWidth(int totalColumns, double cellSize) =>
        EffectiveLeftLabelWidth + totalColumns * cellSize + Math.Max(0, totalColumns - 1) * CellGap;

    /// <summary>The prepared, ready-to-draw shape of the current bindings - computed once per measure
    /// and once per render so the two never disagree about how tall or wide the control is.</summary>
    private readonly record struct PreparedLayout(
        bool HasContent, IReadOnlyList<GridCell?[]> Columns, GridFit Fit, IReadOnlyList<long> SortedNonZeroTotals,
        IReadOnlyList<LegendEntry> ProviderLegend);

    private PreparedLayout Prepare(double availableWidth)
    {
        var rangeStart = RangeStart;
        var rangeEnd = RangeEnd;
        var days = Days;
        // A start still at its default means the bindings have not arrived yet: nothing to lay out.
        if (rangeStart == DateOnly.MinValue || rangeEnd < rangeStart)
            return new PreparedLayout(false, [], default, [], []);

        IReadOnlyList<GridCell?[]> columns;
        if (SingleRow)
        {
            columns = BuildStrip(rangeStart, rangeEnd, Today, days);
        }
        else
        {
            var gridStart = StatsAggregator.WeekStart(rangeStart);
            columns = BuildColumns(gridStart, rangeEnd, Today, days, TotalColumns(gridStart, rangeEnd), HighlightStart, HighlightEnd);
        }
        var totalColumns = columns.Count;
        var fit = FitColumns(totalColumns, Math.Max(0, availableWidth - EffectiveLeftLabelWidth), CellGap, DefaultCellSize, MinCellSize);
        var sortedNonZero = ResolveColorScale(ColorScaleTotals, days, rangeStart, rangeEnd);
        var providerLegend = ShowLegend ? BuildProviderLegend([.. days.Where(day => day.Day >= rangeStart && day.Day <= rangeEnd)]) : [];
        return new PreparedLayout(true, columns, fit, sortedNonZero, providerLegend);
    }

    private int RowCount => SingleRow ? 1 : 7;

    private double ContentHeight(double cellSize) => EffectiveTopLabelHeight + RowCount * cellSize + (RowCount - 1) * CellGap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Prepare(AvailableWidth);
        if (!layout.HasContent)
            return new Size(AvailableWidth, EmptyHeight);

        var width = ContentWidth(layout.Columns.Count, layout.Fit.CellSize);
        if (!ShowLegend)
            return new Size(width, ContentHeight(layout.Fit.CellSize));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var legendRows = LegendFitsOneRow(layout.ProviderLegend, width, dpi, ChartFonts.UiTypeface(this)) ? 1 : 2;
        return new Size(width, ContentHeight(layout.Fit.CellSize) + LegendGapTop + legendRows * LegendRowHeight);
    }

    internal readonly record struct CellHit(Rect Rect, DateOnly Day, long Total, IReadOnlyList<ProviderTotal> ByProvider);

    private readonly List<CellHit> _cellHits = [];
    private CellHit? _hoverCell;

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _cellHits.Clear();

        if (ActualWidth <= 0)
            return;

        var layout = Prepare(AvailableWidth);
        if (!layout.HasContent)
        {
            DrawEmptyText(drawingContext, ActualWidth);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = ChartFonts.UiTypeface(this);

        if (ShowAxisLabels)
        {
            DrawWeekdayLabels(drawingContext, layout.Fit.CellSize, dpi, typeface);
            DrawMonthLabels(drawingContext, layout.Columns, layout.Fit.CellSize, dpi, typeface);
        }
        DrawColumns(drawingContext, layout.Columns, layout.Fit.CellSize, layout.SortedNonZeroTotals);

        if (!ShowLegend)
            return;

        var contentWidth = ContentWidth(layout.Columns.Count, layout.Fit.CellSize);
        var legendY = ContentHeight(layout.Fit.CellSize) + LegendGapTop;
        DrawIntensityScale(drawingContext, legendY, contentWidth, dpi, typeface, layout.ProviderLegend);
        if (layout.ProviderLegend.Count > 0)
        {
            var oneRow = LegendFitsOneRow(layout.ProviderLegend, contentWidth, dpi, typeface);
            DrawProviderLegend(drawingContext, layout.ProviderLegend, oneRow ? legendY : legendY + LegendRowHeight, dpi, typeface);
        }
    }

    private void DrawWeekdayLabels(DrawingContext dc, double cellSize, double dpi, Typeface typeface)
    {
        const DayOfWeek firstDayOfWeek = DayOfWeek.Monday;
        var dayAbbreviations = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (var row = 0; row < 7; row++)
        {
            var dayOfWeek = (DayOfWeek)(((int)firstDayOfWeek + row) % 7);
            if (dayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday))
                continue;

            var y = EffectiveTopLabelHeight + row * (cellSize + CellGap);
            var text = new FormattedText(
                dayAbbreviations[(int)dayOfWeek], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 8, EmptyTextBrush, dpi);
            dc.DrawText(text, new Point(0, y + (cellSize - text.Height) / 2));
        }
    }

    private void DrawMonthLabels(DrawingContext dc, IReadOnlyList<GridCell?[]> columns, double cellSize, double dpi, Typeface typeface)
    {
        foreach (var (column, label) in MonthLabels(columns, CultureInfo.CurrentCulture))
        {
            var x = EffectiveLeftLabelWidth + column * (cellSize + CellGap);
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, TextBrush, dpi);
            // A month that only just began in the last column has no room for its name; a cut-off
            // name reads worse than none.
            if (x + text.Width > ActualWidth)
                continue;
            dc.DrawText(text, new Point(x, 0));
        }
    }

    private void DrawColumns(
        DrawingContext dc, IReadOnlyList<GridCell?[]> columns, double cellSize, IReadOnlyList<long> sortedNonZeroTotals)
    {
        for (var column = 0; column < columns.Count; column++)
        {
            var x = EffectiveLeftLabelWidth + column * (cellSize + CellGap);
            for (var row = 0; row < 7; row++)
            {
                if (columns[column][row] is not { } cell)
                    continue;

                var y = EffectiveTopLabelHeight + row * (cellSize + CellGap);
                var rect = new Rect(x, y, cellSize, cellSize);

                if (cell.IsFuture)
                {
                    dc.PushOpacity(0.4);
                    dc.DrawRoundedRectangle(EmptyCellBrush, null, rect, CellCorner, CellCorner);
                    dc.Pop();
                    continue; // never hit-testable: no tooltip, no click, no outline
                }

                var step = QuantileStep(cell.Total, sortedNonZeroTotals);
                var fill = step == 0 ? EmptyCellBrush : IntensityBrush(ChartPalette.ForProvider(cell.LeaderProviderId), step);
                if (cell.IsOutsideHighlight)
                    dc.PushOpacity(OutsideHighlightOpacity);
                dc.DrawRoundedRectangle(fill, null, rect, CellCorner, CellCorner);
                if (cell.IsOutsideHighlight)
                    dc.Pop();

                if (SelectedDay == cell.Day)
                {
                    // Drawn as a halo just outside the cell itself.
                    var halo = new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3);
                    dc.DrawRoundedRectangle(null, new Pen(AccentBrush, 2), halo, CellCorner + 1, CellCorner + 1);
                }

                if (IsKeyboardFocused && _focusedDay == cell.Day)
                {
                    var ring = new Rect(rect.X - 2.5, rect.Y - 2.5, rect.Width + 5, rect.Height + 5);
                    dc.DrawRoundedRectangle(null, new Pen(AccentBrush, 1.5), ring, CellCorner + 2, CellCorner + 2);
                }

                _cellHits.Add(new CellHit(rect, cell.Day, cell.Total, cell.ByProvider));
            }
        }
    }

    /// <summary>The colour the intensity scale's squares are stepped from: the leading provider's
    /// own colour (the first colour-key entry), so the scale matches the cells it explains, or
    /// <paramref name="neutral"/> while no provider led any day.</summary>
    internal static Color IntensityScaleColor(IReadOnlyList<LegendEntry> providerLegend, Color neutral) =>
        providerLegend.Count > 0 ? ChartPalette.ForProvider(providerLegend[0].ProviderId) : neutral;

    /// <summary>The five-square "less..more" scale, right-aligned in the available width - stepped
    /// in the leading provider's colour with the same alpha steps the cells use (<see
    /// cref="IntensityScaleColor"/>), neutral when no provider led a day.</summary>
    private void DrawIntensityScale(
        DrawingContext dc, double y, double width, double dpi, Typeface typeface, IReadOnlyList<LegendEntry> providerLegend)
    {
        const double square = 10;
        const double gap = 2;

        var lessText = new FormattedText(LegendLessText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 9, EmptyTextBrush, dpi);
        var moreText = new FormattedText(LegendMoreText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 9, EmptyTextBrush, dpi);
        var startX = Math.Max(0, width - IntensityScaleWidth(dpi, typeface));

        dc.DrawText(lessText, new Point(startX, y - 1));
        var x = startX + lessText.Width + 4;
        var scaleColor = IntensityScaleColor(providerLegend, BrushColor(TextBrush));
        for (var step = 0; step <= 4; step++)
        {
            var fill = step == 0 ? EmptyCellBrush : IntensityBrush(scaleColor, step);
            dc.DrawRoundedRectangle(fill, null, new Rect(x, y, square, square), 2, 2);
            x += square;
            if (step < 4)
                x += gap; // no trailing gap after the last square - contentWidth above counts exactly four
        }
        dc.DrawText(moreText, new Point(x + 4, y - 1));
    }

    /// <summary>Width the intensity scale takes at the right edge, its right margin included: without
    /// that margin the "more" label lands flush against the control's clip edge, and font-metric
    /// rounding alone is then enough to clip its last letter.</summary>
    private double IntensityScaleWidth(double dpi, Typeface typeface)
    {
        const double square = 10;
        const double gap = 2;
        const double rightMargin = 4;
        var lessText = new FormattedText(LegendLessText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 9, EmptyTextBrush, dpi);
        var moreText = new FormattedText(LegendMoreText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 9, EmptyTextBrush, dpi);
        return lessText.Width + 4 + 5 * square + 4 * gap + 4 + moreText.Width + rightMargin;
    }

    /// <summary>Width the color key takes, trailing gap after the last entry left out.</summary>
    private double ProviderLegendWidth(IReadOnlyList<LegendEntry> legend, double dpi, Typeface typeface)
    {
        const double swatch = 10;
        var providerNames = ProviderDisplayNames;
        var width = 0.0;
        foreach (var entry in legend)
        {
            var name = providerNames.GetValueOrDefault(entry.ProviderId, entry.ProviderId);
            var nameText = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi);
            width += swatch + 4 + nameText.Width + 16;
        }
        return Math.Max(0, width - 16);
    }

    /// <summary>True when the color key (left) and the intensity scale (right) share one row with
    /// room to spare; a narrow window with many providers moves the key onto its own row below.</summary>
    private bool LegendFitsOneRow(IReadOnlyList<LegendEntry> legend, double width, double dpi, Typeface typeface) =>
        legend.Count == 0 || ProviderLegendWidth(legend, dpi, typeface) + 16 + IntensityScaleWidth(dpi, typeface) <= width;

    /// <summary>The color key, one swatch and display name per provider that led at least one day
    /// this year, largest first, left-aligned on the intensity scale's row (or below it when the two
    /// do not fit side by side).</summary>
    private void DrawProviderLegend(DrawingContext dc, IReadOnlyList<LegendEntry> legend, double y, double dpi, Typeface typeface)
    {
        const double swatch = 10;
        var providerNames = ProviderDisplayNames;
        var x = 0.0;
        foreach (var entry in legend)
        {
            var color = ChartPalette.ForProvider(entry.ProviderId);
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(x, y, swatch, swatch), 2, 2);

            var name = providerNames.GetValueOrDefault(entry.ProviderId, entry.ProviderId);
            var nameText = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi);
            dc.DrawText(nameText, new Point(x + swatch + 4, y - 1));

            x += swatch + 4 + nameText.Width + 16;
        }
    }

    private void DrawEmptyText(DrawingContext dc, double width)
    {
        if (string.IsNullOrEmpty(EmptyText))
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ChartFonts.UiTypeface(this), 12, EmptyTextBrush, dpi);
        dc.DrawText(formatted, new Point(Math.Max(0, (width - formatted.Width) / 2), 20));
    }

    /// <summary>The hover tooltip's own text: the day's date and weekday (a daily bucket never shows
    /// a time, per <see cref="StatsTooltipDateFormatter"/>), then - for a day with usage - every
    /// provider that contributed, largest first, then the day's own total; for a day with none at
    /// all, <paramref name="noUsageText"/> in place of both. Pure so the breakdown is unit testable
    /// without a visual tree, the same shape <see cref="StatsBarChart.BuildTooltipLines"/> already
    /// uses for the other charts' tooltips.</summary>
    internal static IReadOnlyList<string> BuildTooltipLines(
        CellHit hover, IReadOnlyDictionary<string, string> providerDisplayNames, string tokenWord, string totalLabel,
        string noUsageText, CultureInfo culture)
    {
        var lines = new List<string> { StatsTooltipDateFormatter.FormatInstant(hover.Day, StatsTooltipGranularity.Day, culture) };

        if (hover.Total <= 0 || hover.ByProvider.Count == 0)
        {
            if (!string.IsNullOrEmpty(noUsageText))
                lines.Add(noUsageText);
            return lines;
        }

        string WithUnit(long value)
        {
            var number = value.ToString("N0", culture);
            return string.IsNullOrEmpty(tokenWord) ? number : $"{number} {tokenWord}";
        }

        foreach (var entry in hover.ByProvider)
        {
            var name = providerDisplayNames.GetValueOrDefault(entry.ProviderId, entry.ProviderId);
            lines.Add($"{name}: {WithUnit(entry.Total)}");
        }

        var totalLine = WithUnit(hover.Total);
        lines.Add(string.IsNullOrEmpty(totalLabel) ? totalLine : $"{totalLabel}: {totalLine}");
        return lines;
    }

    private ChartTooltipPopup? _tooltipPopup;

    private void ShowTooltip(CellHit hover)
    {
        var lines = BuildTooltipLines(hover, ProviderDisplayNames, TokenWord, TotalLabel, NoUsageText, CultureInfo.CurrentCulture);
        _tooltipPopup ??= new ChartTooltipPopup();
        _tooltipPopup.Show(this, hover.Rect, lines, TooltipBackground, TextBrush, TextBrush);
    }

    private DateOnly? _focusedDay;

    /// <summary>The day the keyboard focus sits on, null while the grid has no focus.</summary>
    internal DateOnly? FocusedDay => _focusedDay;

    /// <summary>Where an arrow key moves the focus, or <paramref name="current"/> when it cannot move
    /// that way. Left and right step to the neighbouring column in the same row (the next or
    /// previous day in a single-row strip), up and down to the day above or below, wrapping into the
    /// neighbouring week; home and end jump to the first and last day. Future days are never
    /// reachable, matching the mouse. A null or unreachable <paramref name="current"/> yields the
    /// first day. Pure so it is testable without a visual tree.</summary>
    internal static DateOnly? MoveFocus(IReadOnlyList<GridCell?[]> columns, DateOnly? current, Key key, bool singleRow)
    {
        var reachable = new List<(int Column, int Row, DateOnly Day)>();
        for (var column = 0; column < columns.Count; column++)
        {
            for (var row = 0; row < 7; row++)
            {
                if (columns[column][row] is { IsFuture: false } cell)
                    reachable.Add((column, row, cell.Day));
            }
        }

        if (reachable.Count == 0)
            return null;

        var index = current is { } day ? reachable.FindIndex(entry => entry.Day == day) : -1;
        if (index < 0)
            return reachable[0].Day;

        var here = reachable[index];
        switch (key)
        {
            case Key.Home:
                return reachable[0].Day;
            case Key.End:
                return reachable[^1].Day;
            case Key.Down:
                return reachable[Math.Min(index + 1, reachable.Count - 1)].Day;
            case Key.Up:
                return reachable[Math.Max(index - 1, 0)].Day;
            case Key.Right when singleRow:
                return reachable[Math.Min(index + 1, reachable.Count - 1)].Day;
            case Key.Left when singleRow:
                return reachable[Math.Max(index - 1, 0)].Day;
            case Key.Right:
                foreach (var entry in reachable)
                {
                    if (entry.Row == here.Row && entry.Column > here.Column)
                        return entry.Day;
                }
                return here.Day;
            case Key.Left:
                for (var i = index - 1; i >= 0; i--)
                {
                    if (reachable[i].Row == here.Row && reachable[i].Column < here.Column)
                        return reachable[i].Day;
                }
                return here.Day;
            default:
                return here.Day;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            if (_focusedDay is { } picked)
            {
                DaySelected?.Invoke(this, picked);
                e.Handled = true;
            }
            return;
        }

        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
            return;

        var layout = Prepare(AvailableWidth);
        if (!layout.HasContent)
            return;

        var moved = MoveFocus(layout.Columns, _focusedDay ?? MoveFocus(layout.Columns, SelectedDay, Key.None, SingleRow), e.Key, SingleRow);
        e.Handled = true;
        if (moved is null || moved == _focusedDay)
            return;

        _focusedDay = moved;
        OnFocusedDayChanged();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        var layout = Prepare(AvailableWidth);
        if (!layout.HasContent)
            return;

        // Start on the selected day when it can be reached, else on the first day.
        _focusedDay = MoveFocus(layout.Columns, SelectedDay, Key.None, SingleRow);
        OnFocusedDayChanged();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_focusedDay is null)
            return;

        _focusedDay = null;
        _tooltipPopup?.Hide();
        AutomationProperties.SetName(this, BuildAccessibleSummary(Days, RangeStart, RangeEnd, CultureInfo.CurrentCulture));
        InvalidateVisual();
    }

    private void OnFocusedDayChanged()
    {
        UpdateFocusedAutomationName();
        InvalidateVisual();
        if (_focusedDay is { } day && _cellHits.FindIndex(hit => hit.Day == day) is var at and >= 0)
            ShowTooltip(_cellHits[at]);
    }

    /// <summary>What a screen reader announces for the focused day: the lines the hover tooltip
    /// shows, joined into one sentence.</summary>
    private void UpdateFocusedAutomationName()
    {
        if (_focusedDay is not { } day)
            return;

        foreach (var column in Prepare(AvailableWidth).Columns)
        {
            foreach (var cell in column)
            {
                if (cell is not { } found || found.Day != day)
                    continue;

                var lines = BuildTooltipLines(
                    new CellHit(default, found.Day, found.Total, found.ByProvider), ProviderDisplayNames, TokenWord, TotalLabel,
                    NoUsageText, CultureInfo.CurrentCulture);
                AutomationProperties.SetName(this, string.Join(". ", lines));
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hit = _cellHits.FirstOrDefault(cell => cell.Rect.Contains(point));
        var hovered = hit.Rect.Width > 0 ? hit : (CellHit?)null;
        if (Nullable.Equals(hovered, _hoverCell))
            return;

        _hoverCell = hovered;
        if (hovered is { } cell)
            ShowTooltip(cell);
        else
            _tooltipPopup?.Hide();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCell is null)
            return;

        _hoverCell = null;
        _tooltipPopup?.Hide();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        var hit = _cellHits.FirstOrDefault(cell => cell.Rect.Contains(point));
        if (hit.Rect.Width <= 0)
            return;

        DaySelected?.Invoke(this, hit.Day);
    }
}
