using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.Stats;

/// <summary>Which of the three by-day-independent data sources the "Per day" panel currently shows -
/// a pure UI view switch, unlike <see cref="StatsGrouping"/> (which also drives the table and the
/// breakdown chart above): all three sources (<see cref="StatsViewModel.PerDayBars"/>, <see
/// cref="StatsViewModel.WeekdayBars"/>, <see cref="StatsViewModel.HourBars"/>) are always kept
/// computed regardless of which one is on screen.</summary>
public enum StatsPerDayView
{
    Day,
    Weekday,
    Hour,
}

/// <summary>
/// Everything the statistics window shows: the period selector, the grouping selector (day, week,
/// model, project), the figures bar (total, per-day average, busiest day, change against the
/// previous period), the input/output/cached breakdown line, the chart and the table. Reads
/// straight from <see cref="StatsStore"/> - nothing here writes back to it, so opening this window
/// twice, or leaving it open across a theme switch, never changes what is stored: it remembers
/// nothing of its own.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly StatsStore _store;
    private readonly Func<string, string?> _askForSavePath;
    private readonly Action<string> _showMessage;

    public ObservableCollection<Choice<string>> RangeChoices { get; } = [];
    public ObservableCollection<Choice<StatsGrouping>> GroupingChoices { get; } = [];
    public ObservableCollection<Choice<StatsPerDayView>> PerDayViewChoices { get; } = [];

    /// <summary>The row a ComboBox binds <c>SelectedItem</c> to - <see cref="Choice{TValue}"/> itself
    /// carries no such property, only <see cref="Choice{TValue}.IsSelected"/> on each row, so this
    /// reads whichever row that flag is currently true on and, on a set, forwards to the same command
    /// a RadioButton group already calls. <see cref="SetRange"/> raises this property's own change
    /// notification too, so a command invoked some other way never leaves the two representations
    /// disagreeing.</summary>
    public Choice<string>? SelectedRangeChoice
    {
        get => RangeChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is not null)
                SetRangeCommand.Execute(value.Value);
        }
    }

    /// <summary>Same bridge as <see cref="SelectedRangeChoice"/>, for the grouping group.</summary>
    public Choice<StatsGrouping>? SelectedGroupingChoice
    {
        get => GroupingChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is not null)
                SetGroupingCommand.Execute(value.Value);
        }
    }

    /// <summary>Same bridge as <see cref="SelectedRangeChoice"/>, for the "Per day" panel's own view
    /// switch.</summary>
    public Choice<StatsPerDayView>? SelectedPerDayViewChoice
    {
        get => PerDayViewChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is not null)
                SetPerDayViewCommand.Execute(value.Value);
        }
    }

    [ObservableProperty]
    private string selectedRange = "Week";

    [ObservableProperty]
    private StatsGrouping selectedGrouping = StatsGrouping.Day;

    /// <summary>Which of <see cref="PerDayBars"/>/<see cref="WeekdayBars"/>/<see cref="HourBars"/>
    /// the "Per day" panel's own chart currently draws - all three stay computed regardless, so
    /// switching this needs no recompute, unlike <see cref="SelectedGrouping"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerDayViewBars))]
    [NotifyPropertyChangedFor(nameof(PerDayViewMaxAxisLabels))]
    [NotifyPropertyChangedFor(nameof(IsPerDayViewHourAxis))]
    [NotifyPropertyChangedFor(nameof(IsPerDayViewStackedByProvider))]
    [NotifyPropertyChangedFor(nameof(PerDayViewTooltip))]
    private StatsPerDayView selectedPerDayView = StatsPerDayView.Day;

    /// <summary>True when the "Per day" panel (and the breakdown chart under the day grouping) draws
    /// one column per calendar week instead of one per day: only for a whole history that spans more
    /// than a year. Up to 366 days every day gets its own column, the 12 month range included. The
    /// table under the day grouping stays per day regardless.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerDayViewTooltip))]
    private bool isWeeklyPerDay;

    private const int WeeklyPerDayThresholdDays = 366;

    /// <summary>The one series actually drawn - <see cref="Views.StatsWindow"/>'s own code-behind
    /// colors it provider-stacked or plain accent depending on <see
    /// cref="IsPerDayViewStackedByProvider"/>, the same split <see cref="Bars"/>/<see
    /// cref="IsStackedByProvider"/> already keep for the breakdown chart above it.</summary>
    public IReadOnlyList<Views.Controls.StatsBarChart.Bar> PerDayViewBars => SelectedPerDayView switch
    {
        StatsPerDayView.Weekday => WeekdayBars,
        StatsPerDayView.Hour => HourBars,
        _ => PerDayBars,
    };

    /// <summary>Only the by-day view stacks by provider - by-weekday and by-hour stay in the plain
    /// theme accent (neither names a single provider), the same reasoning <see
    /// cref="IsStackedByProvider"/> documents for the breakdown chart.</summary>
    public bool IsPerDayViewStackedByProvider => SelectedPerDayView == StatsPerDayView.Day;

    /// <summary>The hour view packs 24 columns into the same width the other two views draw 7-30
    /// columns into, so it keeps the tighter 6-hour label spacing <c>HourChart</c> used to set for
    /// itself alone; the other two views keep the control's own default.</summary>
    public int PerDayViewMaxAxisLabels => SelectedPerDayView == StatsPerDayView.Hour ? 8 : 10;

    /// <summary>True while the "Per day" chart draws the 24 hours, which label their axis at fixed
    /// hours instead of by width.</summary>
    public bool IsPerDayViewHourAxis => SelectedPerDayView == StatsPerDayView.Hour;

    /// <summary>The "Per day" section's own header tooltip, following <see
    /// cref="SelectedPerDayView"/> the same way its title already does through <see
    /// cref="SelectedPerDayViewChoice"/>'s own <see cref="Choice{TValue}.Label"/>.</summary>
    public string PerDayViewTooltip => LocalizationService.Instance[SelectedPerDayView switch
    {
        StatsPerDayView.Weekday => "Tip.Stats.Chart.ByWeekday",
        StatsPerDayView.Hour => "Tip.Stats.Chart.ByHour",
        _ => IsWeeklyPerDay ? "Tip.Stats.Chart.PerWeek" : "Tip.Stats.Chart.PerDay",
    }];

    // The figures bar's own five cards. BusiestDayDateText and the two Change properties are
    // "" / false whenever their underlying figure has nothing to show (an empty period, or - for the
    // change card only - no preceding period to compare against) - shown as an em dash by the view,
    // never a misleading zero. PerActiveDayFigureText follows the same rule for a period with no
    // active days at all.
    [ObservableProperty]
    private string totalFigureText = "";

    [ObservableProperty]
    private string perDayFigureText = "";

    [ObservableProperty]
    private string perActiveDayFigureText = "";

    [ObservableProperty]
    private string busiestDayFigureText = "";

    [ObservableProperty]
    private string busiestDayDateText = "";

    [ObservableProperty]
    private string changeFigureText = "";

    [ObservableProperty]
    private bool changeIsIncrease;

    [ObservableProperty]
    private bool hasPreviousPeriod;

    /// <summary>The change card's caption: names the previous period's own total when there is a
    /// previous period to compare against, otherwise the plain "vs. previous period".</summary>
    [ObservableProperty]
    private string vsPreviousCaption = "";

    // The figures bar's mini charts all draw from these raw numbers instead of the
    // already-formatted *FigureText strings above, which have long since lost the precision (and,
    // for DailyTotalsSeries, the whole daily shape) a chart needs.
    [ObservableProperty]
    private IReadOnlyList<long> dailyTotalsSeries = [];

    [ObservableProperty]
    private long periodTotalRaw;

    [ObservableProperty]
    private long previousPeriodTotalRaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPerActiveDay), nameof(FigureCardColumns))]
    private double activeDayCountRaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPerActiveDay), nameof(FigureCardColumns))]
    private double periodDayCountRaw = 1;

    /// <summary>Whether the "per active day" card earns its place: when every day of the period saw
    /// usage it would only repeat the "per day" card.</summary>
    public bool ShowPerActiveDay => ActiveDayCountRaw < PeriodDayCountRaw;

    /// <summary>The figure cards' column count: five with the "per active day" card, four without.</summary>
    public int FigureCardColumns => ShowPerActiveDay ? 5 : 4;

    [ObservableProperty]
    private string activeDaysOfText = "";

    // The breakdown figures are shortened ("946 M"); the *Exact twins hold the full number for the
    // tooltip.
    [ObservableProperty]
    private string inputText = "";

    [ObservableProperty]
    private string inputExact = "";

    [ObservableProperty]
    private string outputText = "";

    [ObservableProperty]
    private string outputExact = "";

    [ObservableProperty]
    private string cachedText = "";

    [ObservableProperty]
    private string cachedExact = "";

    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsBarChart.Bar> bars = [];

    /// <summary>The one fixed panel - always by-day, provider-stacked, over the selected range;
    /// unlike <see cref="Bars"/> it never follows <see cref="SelectedGrouping"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerDayViewBars))]
    private IReadOnlyList<Views.Controls.StatsBarChart.Bar> perDayBars = [];

    /// <summary>The three donut charts.</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsRingChart.Slice> providerShareSlices = [];

    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsRingChart.Slice> modelShareSlices = [];

    /// <summary>The "share per effort level" ring, beside the model ring - <see
    /// cref="Views.Controls.StatsRingChart.Slice.ProviderId"/> carries the raw, un-localized effort
    /// level here (never a real provider id) so the code-behind can resolve a stable brush by it
    /// regardless of which language <see cref="Views.Controls.StatsRingChart.Slice.Label"/> is
    /// currently shown in.</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsRingChart.Slice> effortShareSlices = [];

    /// <summary>The cache-share bar - always exactly four values, in the fixed order <see
    /// cref="Stats.StatsCacheShareRow.AsValues"/> returns them (new input, cache write, cache read,
    /// output).</summary>
    [ObservableProperty]
    private IReadOnlyList<long> cacheShareValues = [0, 0, 0, 0];

    [ObservableProperty]
    private IReadOnlyList<string> cacheShareLabels = [];

    /// <summary>The "top projects" panel - already sorted longest first, capped at ten rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<StatsProjectRow> topProjectRows = [];

    /// <summary>The "by weekday" panel - seven columns, current-culture day names and week
    /// start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerDayViewBars))]
    private IReadOnlyList<Views.Controls.StatsBarChart.Bar> weekdayBars = [];

    /// <summary>The "by hour" panel - 24 columns, current-culture hour format.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerDayViewBars))]
    private IReadOnlyList<Views.Controls.StatsBarChart.Bar> hourBars = [];

    [ObservableProperty]
    private IReadOnlyList<StatsRowViewModel> rows = [];

    [ObservableProperty]
    private IReadOnlyList<StatsProviderRowViewModel> providerRows = [];

    /// <summary>Throwaway instances built only to read <see cref="Services.IUsageProvider.DisplayName"/> -
    /// the same reasoning <see cref="Views.SettingsWindow.BuildAboutReadLocationsList"/> already uses for its
    /// own "every known provider" list, never a second, hand-typed copy of the same five names.
    /// Public rather than internal: StatsWindow.xaml reaches it through <c>x:Static</c>, which only
    /// ever resolves a public static member, even from inside the same assembly.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderDisplayNames = new Dictionary<string, string>
    {
        ["claude"] = new ClaudeProvider().DisplayName,
        ["codex"] = new CodexProvider().DisplayName,
        ["cursor"] = new CursorProvider().DisplayName,
        ["gemini"] = new GeminiProvider().DisplayName,
        ["copilot"] = new CopilotProvider().DisplayName,
    };

    /// <summary>True only for Day/Week grouping - the chart needs two stacked series brushes then,
    /// one per provider, instead of the single series Model/Project grouping draws.</summary>
    public bool IsStackedByProvider => SelectedGrouping is StatsGrouping.Day or StatsGrouping.Week;

    /// <summary>True only for Day grouping - still gates the plain table (<c>GroupedTable</c>
    /// in StatsWindow.xaml), which the month grid above it covers for Day grouping the same way it
    /// always has; the grid itself no longer depends on this, since it now shows for every
    /// grouping.</summary>
    public bool IsDayGrouping => SelectedGrouping == StatsGrouping.Day;

    /// <summary>The month grid's own input - every day from the grid's start up to today (local time):
    /// the last twelve months, reaching further back on a window wide enough to show more weeks than
    /// that at the normal cell size. Independent of <see cref="SelectedRange"/> and <see cref="SelectedGrouping"/> alike, each
    /// carrying every provider that contributed to it (across all five, not just the two <see
    /// cref="StatsAggregator.StackedProviderOrder"/> stacks) and whichever led, largest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsMonthGrid.DayValue> monthGridDays = [];

    /// <summary>Quantile boundaries of the grid's color scale over the whole recorded history, so
    /// resizing the window (which moves the start) never shifts a day's shade.</summary>
    [ObservableProperty]
    private IReadOnlyList<long> monthGridColorScale = [];

    [ObservableProperty]
    private DateOnly monthGridRangeStart;

    [ObservableProperty]
    private DateOnly monthGridRangeEnd;

    /// <summary>First and last day of the selected period for the grid to emphasise (everything
    /// else draws faded); both null for the whole history and for a period that already covers the
    /// entire grid.</summary>
    [ObservableProperty]
    private DateOnly? monthGridHighlightStart;

    [ObservableProperty]
    private DateOnly? monthGridHighlightEnd;

    /// <summary>Today's own local date - the grid's own future-day cutoff, computed once per
    /// rebuild rather than read from the clock inside the control itself, so the control's own
    /// drawing logic stays pure.</summary>
    [ObservableProperty]
    private DateOnly monthGridToday;

    /// <summary>The month grid's own selection - null for no day picked. Set from <see
    /// cref="ToggleSelectedDay"/>, the handler <see cref="Views.StatsWindow"/> wires to <see
    /// cref="Views.Controls.StatsMonthGrid.DaySelected"/>; also bound back to the grid itself, which
    /// draws the accent outline around whichever day this names.</summary>
    [ObservableProperty]
    private DateOnly? selectedDay;

    public bool HasSelectedDay => SelectedDay is not null;

    [ObservableProperty]
    private string selectedDayHeadText = "";

    /// <summary>The day-detail panel's own four charts, already shaped for display the same way <see
    /// cref="Rows"/> already is - built from <see cref="StatsAggregator.DayDetail"/>'s raw totals in
    /// <see cref="ToggleSelectedDay"/>, never bound directly, so the view stays a plain binding.
    /// <see cref="ProviderIds"/>/<see cref="ProviderValues"/> feed a stacked share bar (<see
    /// cref="Views.Controls.StatsCacheShareBar"/> reused for a provider breakdown instead of the cache
    /// kinds it was built for) - the raw ids so the code-behind can resolve each one's own <see
    /// cref="ChartPalette.ForProvider"/> brush, largest first. <see cref="ModelRows"/> and <see
    /// cref="ProjectRows"/> reuse the "top projects" horizontal bar chart's own row shape, largest
    /// first, with <see cref="StatsProjectRow.FirstActivity"/>/<see cref="StatsProjectRow.LastActivity"/>
    /// both pinned to the selected day itself (single-day, so there is no real range). <see
    /// cref="HourBars"/> is the one exception to "largest first": a bar chart's X axis has to read left
    /// to right as hour 0 through 23, not by size - <see cref="StatsAggregator.DayDetail"/>'s own
    /// <c>ByHour</c> sorts by size like the other three, so this re-sorts it by each slice's hour
    /// number.</summary>
    public readonly record struct DayDetailView(
        IReadOnlyList<string> ProviderIds,
        IReadOnlyList<long> ProviderValues,
        IReadOnlyList<string> ProviderLabels,
        IReadOnlyList<StatsProjectRow> ModelRows,
        IReadOnlyList<StatsProjectRow> ProjectRows,
        IReadOnlyList<Views.Controls.StatsBarChart.Bar> HourBars)
    {
        public static readonly DayDetailView Empty = new([], [], [], [], [], []);
    }

    [ObservableProperty]
    private DayDetailView selectedDayDetail = DayDetailView.Empty;

    /// <summary>The records the currently shown period holds - kept from the last <see
    /// cref="RecomputeFrom"/> so a day click can build its own detail without re-reading the whole
    /// store.</summary>
    private IReadOnlyList<StatsRecord> _currentPeriodRecords = [];

    /// <summary>Every stored record, kept for the day detail: the day grid spans the whole year
    /// whatever period is selected, so a day clicked there must not be looked up in the selected
    /// period only (a colored day outside a seven-day period used to show zero tokens).</summary>
    private IReadOnlyList<StatsRecord> _allRecords = [];

    /// <summary>True when the selected day has any usage; the detail charts show only then.</summary>
    public bool SelectedDayHasUsage { get; private set; }

    /// <summary>True for a selected day without usage: one plain line replaces the empty charts.</summary>
    public bool SelectedDayIsEmpty => HasSelectedDay && !SelectedDayHasUsage;

    /// <summary>The month grid's own click handler: a second click on the already-selected day clears
    /// the selection again, any other day replaces it - the toggle <see cref="Views.StatsWindow"/>'s
    /// own <c>DaySelected</c> handler forwards every click to.</summary>
    public void ToggleSelectedDay(DateOnly day)
    {
        SelectedDay = SelectedDay == day ? null : day;
    }

    /// <summary>The day-detail box's close button: clears the selection, the same state a second
    /// click on the selected day reaches.</summary>
    [RelayCommand]
    private void CloseDayDetail() => SelectedDay = null;

    /// <summary>A day picked outside this window (the widget's own day grid). Like a click here it
    /// stays through the rebuilds that follow on their own - the first load of a window it just
    /// opened, an index refresh - until the user changes the period, the grouping or the day.</summary>
    public void SelectDay(DateOnly day) => SelectedDay = day;

    partial void OnSelectedDayChanged(DateOnly? value)
    {
        OnPropertyChanged(nameof(HasSelectedDay));
        RebuildSelectedDayDetail();
    }

    /// <summary>Builds the day detail from <see cref="_allRecords"/> - run on a changed selection and
    /// again whenever the records themselves change under an unchanged selection.</summary>
    private void RebuildSelectedDayDetail()
    {
        if (SelectedDay is not { } day)
        {
            SelectedDayHasUsage = false;
            OnPropertyChanged(nameof(SelectedDayHasUsage));
            OnPropertyChanged(nameof(SelectedDayIsEmpty));
            SelectedDayDetail = DayDetailView.Empty;
            SelectedDayHeadText = "";
            return;
        }

        var detail = StatsAggregator.DayDetail(_allRecords, day, LocalizationService.Instance["Stats.NoProject"], CultureInfo.CurrentCulture);
        SelectedDayHasUsage = detail.ByProvider.Sum(slice => slice.Total) > 0;
        OnPropertyChanged(nameof(SelectedDayHasUsage));
        OnPropertyChanged(nameof(SelectedDayIsEmpty));
        SelectedDayDetail = new DayDetailView(
            detail.ByProvider.Select(slice => slice.Label).ToList(),
            detail.ByProvider.Select(slice => slice.Total).ToList(),
            detail.ByProvider.Select(slice => ProviderDisplayNames.GetValueOrDefault(slice.Label, slice.Label)).ToList(),
            ToModelRows(detail.ByModel, day),
            ToDayProjectRows(detail.ByProject, day, ProjectColors),
            ToHourBars(detail.ByHour));

        // The day's projects that have no resolved color yet are resolved in the background; the
        // detail is built again once they arrive (see OnProjectColorsChanged).
        var noProject = LocalizationService.Instance["Stats.NoProject"];
        _dayProjectKeys = detail.ByProject.Select(slice => slice.Label).Where(key => key != noProject).ToList();
        var unrequested = _dayProjectKeys.Where(key => !ProjectColors.ContainsKey(key) && _requestedDayProjectKeys.Add(key)).ToList();
        if (unrequested.Count > 0)
            RefreshProjectColors(TopProjectRows, CurrentBarProjectKeys());

        var loc = LocalizationService.Instance;
        var dayTotal = detail.ByProvider.Sum(slice => slice.Total);
        var dateText = day.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);
        var shortened = StatsAggregator.ShortenTokenCount(dayTotal, loc["Number.Thousand"], loc["Number.Million"], loc["Number.Billion"]);
        SelectedDayHeadText = string.Format(CultureInfo.CurrentCulture, loc["Stats.DayDetail.Title"], $"{dateText} · {shortened} {loc["Stats.TokenUnit"]}");
    }

    // Bumped by every call, so a slow resolution from an earlier, already-superseded period can
    // never overwrite the rows a later one already put in TopProjectRows - the same "only the
    // newest request wins" guard RecomputeAsync applies to a whole rebuild.
    private int _projectColorGeneration;

    // The project folders of the selected day's detail, resolved together with the period's own so
    // that a newer request never discards the keys of an older one that is still running.
    private IReadOnlyList<string> _dayProjectKeys = [];
    private readonly HashSet<string> _requestedDayProjectKeys = [];

    private List<string> CurrentBarProjectKeys() => SelectedGrouping == StatsGrouping.Project
        ? Bars.Select(bar => bar.ColorProviderId).Where(key => key.Length > 0 && key != OtherProjectsColorKey).ToList()
        : [];

    partial void OnProjectColorsChanged(IReadOnlyDictionary<string, System.Windows.Media.Color> value)
    {
        if (SelectedDay is not null)
            RebuildSelectedDayDetail();
    }

    /// <summary>Resolves and merges each row's own <see cref="StatsProjectRow.Color"/>/<see
    /// cref="StatsProjectRow.IconPath"/> in the background, off this method's own caller's thread -
    /// <see cref="ProjectColorResolver"/> walks folders and decodes icon files, neither of which
    /// belongs on the thread that also has to keep the window drawing. <see cref="TopProjectRows"/>
    /// itself is set synchronously beforehand with every other field already filled in, so the panel
    /// never waits on this before showing anything - it just redraws a second time, in color, once
    /// this finishes.</summary>
    private void RefreshProjectColors(IReadOnlyList<StatsProjectRow> rows, IReadOnlyList<string> barProjects) =>
        _ = RefreshProjectColorsAsync(rows, barProjects);

    /// <summary>Each project folder's own color, the same one its "top projects" row shows - the
    /// project grouping's bars look their color up here, so a project never has two colors on this
    /// window. Filled in the background by <see cref="RefreshProjectColorsAsync"/>; a project not
    /// in here yet draws in a fallback hue until it is.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, System.Windows.Media.Color> projectColors = new Dictionary<string, System.Windows.Media.Color>();

    /// <summary>The actual work behind <see cref="RefreshProjectColors"/> - a plain <c>await</c>
    /// rather than <see cref="TaskScheduler.FromCurrentSynchronizationContext"/>, since the latter
    /// throws outright on any thread with no real <see cref="SynchronizationContext"/> installed (a
    /// unit test's own thread, most of the time) - <c>await</c> degrades gracefully there instead,
    /// simply resuming on a thread pool thread, while still marshalling back to the window's own UI
    /// thread in the real, running app.</summary>
    private async Task RefreshProjectColorsAsync(IReadOnlyList<StatsProjectRow> rows, IReadOnlyList<string> barProjects)
    {
        var generation = ++_projectColorGeneration;
        var folders = rows.Select(row => row.FullPath).Concat(barProjects).Concat(_dayProjectKeys).Distinct().ToList();
        if (folders.Count == 0)
            return;

        var cacheFilePath = Path.Combine(AppPaths.DataDirectory, "project-colors.json");
        IReadOnlyDictionary<string, ProjectColorInfo> resolved;
        try
        {
            resolved = await Task.Run(() => ProjectColorResolver.ResolveAll(folders, cacheFilePath));
        }
        catch (Exception)
        {
            return;
        }

        if (generation != _projectColorGeneration)
            return;

        TopProjectRows = TopProjectRows
            .Select(row => resolved.TryGetValue(row.FullPath, out var info)
                ? row with { Color = info.Color, IconPath = info.IconPath }
                : row)
            .ToList();

        var colors = new Dictionary<string, System.Windows.Media.Color>(ProjectColors);
        foreach (var (folder, info) in resolved)
            colors[folder] = info.Color;
        ProjectColors = colors;
    }

    /// <summary>The day-detail model slices recast as "top projects"-style rows - the day fills both
    /// <see cref="StatsProjectRow.FirstActivity"/> and <see cref="StatsProjectRow.LastActivity"/>
    /// since a single day's own breakdown has no real range to show, only the model's own share of
    /// that one day. Each model takes the color the model ring gives it: its provider's color, lighter
    /// by its rank among that provider's models in this list.</summary>
    private static List<StatsProjectRow> ToModelRows(IReadOnlyList<StatsShareSlice> slices, DateOnly day)
    {
        var rankByProvider = new Dictionary<string, int>();
        return slices
            .Select(slice =>
            {
                var row = new StatsProjectRow(slice.Label, slice.Label, slice.Total, slice.Percent, day, day);
                if (string.IsNullOrEmpty(slice.ProviderId))
                    return row;

                var rank = rankByProvider.GetValueOrDefault(slice.ProviderId);
                rankByProvider[slice.ProviderId] = rank + 1;
                return row with { Color = ChartPalette.ForModel(slice.ProviderId, rank) };
            })
            .ToList();
    }

    /// <summary>The day-detail project slices as rows, in the project's own color from <paramref
    /// name="projectColors"/> (the one its other panels show), or the fallback hue the project chart
    /// uses until that color is resolved.</summary>
    private static List<StatsProjectRow> ToDayProjectRows(
        IReadOnlyList<StatsShareSlice> slices, DateOnly day, IReadOnlyDictionary<string, System.Windows.Media.Color> projectColors) =>
        slices
            .Select(slice =>
            {
                var shortened = StatsAggregator.MiddleEllipsis(StatsAggregator.ShortenProjectLabel(slice.Label), StatsAggregator.ProjectLabelMaxChars);
                var color = projectColors.TryGetValue(slice.Label, out var resolved) && resolved != default
                    ? resolved
                    : ChartPalette.ForProvider(slice.Label);
                return new StatsProjectRow(shortened, slice.Label, slice.Total, slice.Percent, day, day, Color: color);
            })
            .ToList();

    /// <summary>The day-detail hour chart's own bars, re-sorted from <see
    /// cref="StatsAggregator.DayDetail"/>'s size-descending <c>ByHour</c> back into chronological
    /// order by each slice's hour number, whatever the culture's label looks like; every one of the 24 hours is always present (an unused one
    /// summing to zero) once the day has any record at all.</summary>
    private static List<Views.Controls.StatsBarChart.Bar> ToHourBars(IReadOnlyList<StatsShareSlice> byHour) =>
        byHour
            .OrderBy(slice => slice.Hour)
            .Select(slice => new Views.Controls.StatsBarChart.Bar(slice.Label, [slice.Total]))
            .ToList();

    /// <summary>Display names in the exact order <see cref="StatsAggregator.StackedProviderOrder"/>
    /// stacks segments in - what a stacked chart's hover tooltip names each segment with.</summary>
    public static IReadOnlyList<string> StackedProviderDisplayNames { get; } =
        StatsAggregator.StackedProviderOrder.Select(id => ProviderDisplayNames.GetValueOrDefault(id, id)).ToList();

    public StatsViewModel(StatsStore store, Func<string, string?>? askForSavePath = null, Action<string>? showMessage = null)
    {
        _store = store;
        LoadRecords = () => StatsAggregator.ResolveBareProjectNames(_store.LoadAll());
        _askForSavePath = askForSavePath ?? ShowRealSaveDialog;
        _showMessage = showMessage ?? (message => Views.ConfirmWindow.ShowInfo(null, message));

        RangeChoices.Add(new Choice<string>("Chart.Range.Week", "Week"));
        RangeChoices.Add(new Choice<string>("Chart.Range.Month", "Month"));
        RangeChoices.Add(new Choice<string>("StatsWindow.Range.TwelveMonths", "Year"));
        RangeChoices.Add(new Choice<string>("Chart.Range.All", "All"));
        Choice.Select(RangeChoices, SelectedRange);

        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByDay", StatsGrouping.Day));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByWeek", StatsGrouping.Week));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByModel", StatsGrouping.Model));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByProject", StatsGrouping.Project));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByEffort", StatsGrouping.Effort));
        Choice.Select(GroupingChoices, SelectedGrouping);

        PerDayViewChoices.Add(new Choice<StatsPerDayView>("Stats.Chart.PerDay", StatsPerDayView.Day));
        PerDayViewChoices.Add(new Choice<StatsPerDayView>("Stats.Chart.ByWeekday", StatsPerDayView.Weekday));
        PerDayViewChoices.Add(new Choice<StatsPerDayView>("Stats.Chart.ByHour", StatsPerDayView.Hour));
        Choice.Select(PerDayViewChoices, SelectedPerDayView);

        // Nothing is read here: the window loads once it is shown (see StatsWindow), so opening it
        // never waits on the database.
        IsLoading = true;
    }

    /// <summary>True while the records for a newly picked period are still being read. Everything
    /// before the first await runs on the caller's own thread, so the selector and the busy flag are
    /// both already set by the time the command hands control back - only the read itself waits.</summary>
    [ObservableProperty]
    private bool isLoading;

    [RelayCommand]
    private async Task SetRange(string range)
    {
        if (range == SelectedRange)
            return;
        SelectedDay = null;
        SelectedRange = range;
        Choice.Select(RangeChoices, SelectedRange);
        OnPropertyChanged(nameof(SelectedRangeChoice));
        await RecomputeForSelectionAsync();
    }

    [RelayCommand]
    private async Task SetGrouping(StatsGrouping grouping)
    {
        if (grouping == SelectedGrouping)
            return;
        SelectedDay = null;
        SelectedGrouping = grouping;
        Choice.Select(GroupingChoices, SelectedGrouping);
        OnPropertyChanged(nameof(SelectedGroupingChoice));
        OnPropertyChanged(nameof(IsStackedByProvider));
        OnPropertyChanged(nameof(IsDayGrouping));
        await RecomputeForSelectionAsync();
    }

    /// <summary>Switches the "Per day" panel's own chart between its three always-computed sources -
    /// no recompute, unlike <see cref="SetGrouping"/>, since none of the three depends on which one
    /// is currently on screen.</summary>
    [RelayCommand]
    private void SetPerDayView(StatsPerDayView view)
    {
        if (view == SelectedPerDayView)
            return;
        SelectedPerDayView = view;
        Choice.Select(PerDayViewChoices, SelectedPerDayView);
        OnPropertyChanged(nameof(SelectedPerDayViewChoice));
    }

    // Every run takes the next number; only the newest run may publish its result or clear
    // IsLoading, so two overlapping runs can never leave the older one's data on screen.
    private int _recomputeGeneration;

    /// <summary>How long a run must take before <see cref="IsLoading"/> turns on, so a quick rebuild
    /// never flashes the "loading" state.</summary>
    internal TimeSpan LoadingDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Reads and resolves every stored record - a seam so a test can slow a run down.</summary>
    internal Func<IReadOnlyList<StatsRecord>> LoadRecords { get; set; }

    /// <summary>A period or grouping pick: the records are already in memory once a load has finished
    /// (only a window open or an index update changes them), so the rebuild reuses them instead of
    /// reading the whole store again. Before that first load it is the normal read.</summary>
    private Task RecomputeForSelectionAsync()
    {
        if (!_recordsLoaded)
            return RecomputeAsync();

        RecomputeFrom(_allRecords);
        return Task.CompletedTask;
    }

    // Set once records read from the store have been shown; _allRecords is empty-but-valid before it.
    private bool _recordsLoaded;

    /// <summary>The same rebuild as <see cref="Recompute"/>, with the one part that can take real
    /// time - reading every stored record - moved off the window's own thread, so picking the whole
    /// history keeps the window drawing and answering instead of standing still until it is done.
    /// Everything that touches a bound property still runs on the calling thread afterwards. The
    /// loading state only shows once the run has taken longer than <see cref="LoadingDelay"/>.</summary>
    public async Task RecomputeAsync()
    {
        var generation = ++_recomputeGeneration;
        using var delayCancellation = new CancellationTokenSource();
        _ = ShowLoadingAfterDelayAsync(generation, delayCancellation.Token);
        try
        {
            var all = await Task.Run(LoadRecords);
            if (generation == _recomputeGeneration)
            {
                RecomputeFrom(all);
                _recordsLoaded = true;
            }
        }
        finally
        {
            delayCancellation.Cancel();
            if (generation == _recomputeGeneration)
                IsLoading = false;
        }
    }

    private async Task ShowLoadingAfterDelayAsync(int generation, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(LoadingDelay, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation == _recomputeGeneration && !cancellation.IsCancellationRequested)
            IsLoading = true;
    }

    /// <summary>Reads every stored record and rebuilds the chart, the table and the headline
    /// figures for the currently selected period and grouping - the one place this view model
    /// touches <see cref="StatsStore"/>, called once when the window loads and again on every period or
    /// grouping change.</summary>
    public void Recompute()
    {
        RecomputeFrom(LoadRecords());
        _recordsLoaded = true;
        IsLoading = false;
    }

    /// <summary>The rebuild itself, on records already read - the one half <see
    /// cref="RecomputeAsync"/> keeps on the window's own thread.</summary>
    private void RecomputeFrom(IReadOnlyList<StatsRecord> all)
    {
        // Days are bucketed in local time, so "today" is the local calendar day too.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = RangeStart(SelectedRange, today);

        var inRange = all.Where(record => record.Day >= from && record.Day <= today).ToList();
        var previous = PreviousPeriod(all, SelectedRange, from, today);
        _currentPeriodRecords = inRange;
        _allRecords = all;
        // The selected day stays across a rebuild (an index refresh must not drop what the user
        // clicked; a period or grouping change clears it before it gets here), but its detail was
        // built from the previous records.
        RebuildSelectedDayDetail();

        // Which day the gap-filled by-day rows start at. Not "from": the whole history has no lower
        // bound at all, so filling every empty day from there would build one bar per day since the
        // year 1 - about 740,000 of them, which is what made the window stop answering and then give
        // up. The earliest day actually stored is the first one worth drawing.
        var gapFillFrom = GapFillStart(from, inRange, today);

        var weeklyPerDay = SelectedRange == "All" && today.DayNumber - gapFillFrom.DayNumber + 1 > WeeklyPerDayThresholdDays;
        IsWeeklyPerDay = weeklyPerDay;
        UpdatePerDayChoiceLabel(weeklyPerDay);

        // The table under the day grouping stays per day; only the bars are bundled per week.
        var dayRows = SelectedGrouping == StatsGrouping.Day
            ? StatsAggregator.Group(inRange, StatsGrouping.Day, gapFillFrom, today)
            : null;
        var weekRows = weeklyPerDay ? StatsAggregator.Group(inRange, StatsGrouping.Week, gapFillFrom, today) : null;

        // Week grouping keeps its empty weeks as zero rows, the same way the day bars keep empty days.
        var groupedRows = SelectedGrouping switch
        {
            StatsGrouping.Day => weekRows ?? dayRows!,
            StatsGrouping.Week => weekRows ?? StatsAggregator.Group(inRange, StatsGrouping.Week, gapFillFrom, today),
            _ => StatsAggregator.Group(inRange, SelectedGrouping, noProjectLabel: LocalizationService.Instance["Stats.NoProject"]),
        };

        // A model/effort/project bar gets its own color - the same one its ring slice (or, once
        // project colors exist, its own swatch) uses - rather than the flat theme accent every other
        // grouping keeps. A model row's label is already its display name, so the provider map below
        // is keyed by the display name too.
        var barColorKeys = BuildBarColorKeys(SelectedGrouping, groupedRows, inRange);

        if (SelectedGrouping == StatsGrouping.Effort)
            groupedRows = groupedRows.Select(row => row with { Label = ResolveEffortLabel(row.Label) }).ToList();
        // Only the bars are bundled: the table keeps every project.
        var barRows = groupedRows;
        var barKeys = barColorKeys;
        string? otherTooltipLine = null;
        if (SelectedGrouping == StatsGrouping.Project && groupedRows.Count > MaxProjectBars + 1)
        {
            var rest = groupedRows.Skip(MaxProjectBars).ToList();
            var restTotal = rest.Sum(row => row.Total);
            barRows = groupedRows.Take(MaxProjectBars)
                .Append(new StatsGroupedRow(LocalizationService.Instance["Stats.OtherProjects"], [restTotal], restTotal)).ToList();
            barKeys = barColorKeys.Take(MaxProjectBars).Append(new BarColorKey(OtherProjectsColorKey, 0)).ToList();
            otherTooltipLine = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Stats.OtherProjects.Count"], rest.Count);
        }

        Bars = barRows.Select((row, index) => new Views.Controls.StatsBarChart.Bar(
            row.Label, row.StackedValues, barKeys[index].ProviderId, barKeys[index].Rank,
            barKeys[index].ProviderId == OtherProjectsColorKey ? otherTooltipLine : null)).ToList();

        // The dedicated "Per day" panel always shows by-day, provider-stacked, independent of
        // whichever grouping the explorer chart/table above is currently set to - reusing the exact
        // same gap-filled rows when the grouping already happens to be Day, computing them fresh
        // otherwise.
        var perDayRows = weekRows
            ?? dayRows
            ?? StatsAggregator.Group(inRange, StatsGrouping.Day, gapFillFrom, today);
        PerDayBars = perDayRows.Select(row => new Views.Controls.StatsBarChart.Bar(row.Label, row.StackedValues)).ToList();

        // The month grid's own by-day list: the last twelve months up to today (local time), or further
        // back for a wide window (see MonthGridAvailableWidth), never the selected range or grouping -
        // unlike PerDayBars, stacked by all five providers rather than just the two
        // StackedProviderOrder names, since any of them can lead a day on this grid. Built by
        // StatsMonthGridBuilder, the one place this computation lives - the widget's own day-grid tile
        // (DayGridTileViewModel) reads the same store through the same builder, so the two never
        // disagree about a day's color or total.
        _monthGridToday = DateOnly.FromDateTime(DateTime.Now);
        _monthGridPeriodStart = from;
        _monthGridPeriodEnd = today;
        RebuildMonthGrid(all, colorScale: null);

        Rows = (dayRows ?? groupedRows)
            .Select(row =>
            {
                var totalText = row.Total.ToString("N0", CultureInfo.CurrentCulture);
                if (SelectedGrouping != StatsGrouping.Project)
                    return new StatsRowViewModel(row.Label, row.Total, totalText);

                var shortened = StatsAggregator.MiddleEllipsis(StatsAggregator.ShortenProjectLabel(row.Label), StatsAggregator.ProjectLabelMaxChars);
                return new StatsRowViewModel(shortened, row.Total, totalText, ToolTip: row.Label);
            })
            .ToList();

        var summary = StatsAggregator.Summarize(inRange, previous);
        var loc = LocalizationService.Instance;

        // A provider without local token counts (Gemini/Antigravity, Copilot) is dropped
        // entirely rather than shown with an explanatory sentence - not here, and not in any chart,
        // legend or grouping either, since none of those ever aggregate a record this provider never
        // wrote to the store in the first place.
        ProviderRows = ProviderCoverage.AllProviderIds
            .Where(ProviderCoverage.HasLocalTokenData)
            .Select(providerId =>
            {
                var displayName = ProviderDisplayNames.GetValueOrDefault(providerId, providerId);
                var providerTotal = inRange.Where(record => record.Provider == providerId).Sum(record => record.TotalTokens);
                return new StatsProviderRowViewModel(displayName, ShortenTokens(providerTotal, loc), providerTotal.ToString("N0", CultureInfo.CurrentCulture));
            })
            .ToList();

        InputText = LabelValue(loc, "Stats.Input", ShortenTokens(summary.InputTokens, loc));
        InputExact = summary.InputTokens.ToString("N0", CultureInfo.CurrentCulture);
        OutputText = LabelValue(loc, "Stats.Output", ShortenTokens(summary.OutputTokens, loc));
        OutputExact = summary.OutputTokens.ToString("N0", CultureInfo.CurrentCulture);
        CachedText = LabelValue(loc, "Stats.Cached", ShortenTokens(summary.CacheTokens, loc));
        CachedExact = summary.CacheTokens.ToString("N0", CultureInfo.CurrentCulture);

        // The two donut charts - provider stays in the fixed StackedProviderOrder (same colour
        // convention the stacked charts already use), model is sorted by size with everything past
        // the largest six pooled into one "Other"/"Andere" entry.
        var million = loc["Number.Million"];
        var billion = loc["Number.Billion"];

        var providerShares = StatsAggregator.ShareByProvider(inRange);
        ProviderShareSlices = providerShares
            .Select(slice => new Views.Controls.StatsRingChart.Slice(
                ProviderDisplayNames.GetValueOrDefault(slice.Label, slice.Label),
                slice.Percent,
                ProviderId: slice.Label,
                Total: slice.Total))
            .ToList();

        var otherLabel = loc["Stats.Other"];
        var modelShares = StatsAggregator.ShareByModel(inRange, topCount: 6, otherLabel);
        ModelShareSlices = modelShares
            .Select(slice => new Views.Controls.StatsRingChart.Slice(
                slice.Label, slice.Percent, slice.ProviderId, Total: slice.Total))
            .ToList();

        // The "share per effort level" ring, beside the model ring. ProviderId here carries the raw
        // effort level (never a real provider id) - the code-behind resolves a stable brush by it, the
        // same seam ShareByProvider's own ProviderId already serves for a provider slice.
        var effortShares = StatsAggregator.ShareByEffort(inRange);
        EffortShareSlices = effortShares
            .Select(slice => new Views.Controls.StatsRingChart.Slice(ResolveEffortLabel(slice.Label), slice.Percent, ProviderId: slice.Label, Total: slice.Total))
            .ToList();

        // The cache-share bar.
        var cacheShare = StatsAggregator.CacheShare(inRange);
        CacheShareValues = cacheShare.AsValues;
        CacheShareLabels = [loc["Stats.Cache.New"], loc["Stats.Cache.Write"], loc["Stats.Cache.Read"], loc["Stats.Cache.Output"]];

        // The "top projects" panel.
        TopProjectRows = StatsAggregator.TopProjectsDetailed(inRange, limit: 12, loc["Stats.NoProject"]);
        RefreshProjectColors(TopProjectRows, CurrentBarProjectKeys());

        // The "by weekday" panel.
        var weekdayRows = StatsAggregator.GroupByWeekday(inRange, CultureInfo.CurrentCulture);
        WeekdayBars = weekdayRows.Select(row => new Views.Controls.StatsBarChart.Bar(row.Label, row.StackedValues)).ToList();

        // The "by hour" panel.
        var hourRows = StatsAggregator.GroupByHour(inRange, CultureInfo.CurrentCulture);
        HourBars = hourRows.Select(row => new Views.Controls.StatsBarChart.Bar(row.Label, row.StackedValues)).ToList();

        var periodDayCount = PeriodDayCount(SelectedRange, from, today, all);
        // The preceding period always has the range's fixed length, unlike periodDayCount, which a
        // short history can cut down.
        var figures = StatsAggregator.ComputeHeadlineFigures(inRange, previous, periodDayCount, today.DayNumber - from.DayNumber + 1);
        ApplyHeadlineFigures(figures, previous.Sum(record => record.TotalTokens), loc);

        // The figures bar's mini charts - see the raw properties' own doc comments.
        // Always the period's own last periodDayCount days: "from" is DateOnly.MinValue for the whole
        // history, which would put every record outside the series.
        DailyTotalsSeries = StatsAggregator.DailyTotalsSeries(inRange, today.AddDays(-(Math.Max(1, periodDayCount) - 1)), periodDayCount);
        PeriodTotalRaw = figures.Total;
        PreviousPeriodTotalRaw = previous.Sum(record => record.TotalTokens);
        ActiveDayCountRaw = figures.ActiveDayCount;
        PeriodDayCountRaw = Math.Max(1, periodDayCount);
        ActiveDaysOfText = string.Format(CultureInfo.CurrentCulture, loc["Stats.ActiveDaysOf"], figures.ActiveDayCount, periodDayCount);
    }

    private static string LabelValue(LocalizationService loc, string labelKey, string value) =>
        loc.Format("Stats.LabelValue", loc[labelKey], value);

    /// <summary>The current period's own calendar-day length that the figures bar's "per day"
    /// average divides by: the fixed length <see cref="RangeStart"/> used for every named range, but
    /// never longer than the span from the earliest record ever stored up to today - a 12 month
    /// range over three months of data averages over those three months, not over a full year. "All" has
    /// no fixed length and takes that span as is. With no records at all it falls back to one
    /// day.</summary>
    private static int PeriodDayCount(string range, DateOnly from, DateOnly today, IReadOnlyList<StatsRecord> all)
    {
        if (all.Count == 0)
            return 1;

        var sinceFirstRecord = Math.Max(1, today.DayNumber - all.Min(record => record.Day).DayNumber + 1);
        return range == "All" ? sinceFirstRecord : Math.Min(today.DayNumber - from.DayNumber + 1, sinceFirstRecord);
    }

    /// <summary>The first entry of <see cref="PerDayViewChoices"/> reads "Per week" while the chart
    /// bundles by week and "Per day" otherwise. The entry is swapped for a freshly keyed one only
    /// when the wording actually changes, keeping whichever view is selected.</summary>
    private void UpdatePerDayChoiceLabel(bool weekly)
    {
        if (PerDayViewChoices.Count == 0)
            return;

        var key = weekly ? "Stats.Chart.PerWeek" : "Stats.Chart.PerDay";
        if (_perDayChoiceKey == key)
            return;

        _perDayChoiceKey = key;
        PerDayViewChoices[0] = new Choice<StatsPerDayView>(key, StatsPerDayView.Day);
        Choice.Select(PerDayViewChoices, SelectedPerDayView);
        OnPropertyChanged(nameof(SelectedPerDayViewChoice));
    }

    private string _perDayChoiceKey = "Stats.Chart.PerDay";

    /// <summary>A language switch while the window is open: the choice labels are looked up again
    /// and every text this view model composes itself (figures, labels, the day heading) is rebuilt
    /// from the records already loaded - no new read.</summary>
    public void RefreshLanguage()
    {
        foreach (var choice in RangeChoices)
            choice.RefreshLabel();
        foreach (var choice in GroupingChoices)
            choice.RefreshLabel();
        foreach (var choice in PerDayViewChoices)
            choice.RefreshLabel();
        OnPropertyChanged(nameof(PerDayViewTooltip));
        RecomputeFrom(_allRecords);
    }

    private static string ShortenTokens(long value, LocalizationService loc) =>
        StatsAggregator.ShortenTokenCount(value, loc["Number.Thousand"], loc["Number.Million"], loc["Number.Billion"]);

    /// <summary>Formats <see cref="StatsAggregator.ComputeHeadlineFigures"/>'s pure result into the
    /// four bound strings the view shows - the only place a shortened number or an em dash for "no
    /// figure to show" gets decided.</summary>
    private void ApplyHeadlineFigures(StatsHeadlineFigures figures, long previousTotal, LocalizationService loc)
    {
        var thousand = loc["Number.Thousand"];
        var million = loc["Number.Million"];
        var billion = loc["Number.Billion"];

        TotalFigureText = StatsAggregator.ShortenTokenCount(figures.Total, thousand, million, billion);
        // Round-half-up by hand: figures.PerDayAverage is a token count divided by a positive day
        // count, so it is never negative, and every other rounding call in this codebase funnels
        // through one shared helper (StatusTextMapTests enforces it) - one built for a usage
        // percentage, not a plain token average, so it does not fit here.
        PerDayFigureText = StatsAggregator.ShortenTokenCount((long)(figures.PerDayAverage + 0.5), thousand, million, billion);

        PerActiveDayFigureText = figures.ActiveDayCount > 0
            ? StatsAggregator.ShortenTokenCount((long)((double)figures.Total / figures.ActiveDayCount + 0.5), thousand, million, billion)
            : "–";

        if (figures.BusiestDay is { } busiestDay)
        {
            BusiestDayFigureText = StatsAggregator.ShortenTokenCount(figures.BusiestDayTotal, thousand, million, billion);
            BusiestDayDateText = StatsTooltipDateFormatter.FormatInstant(busiestDay, StatsTooltipGranularity.Day, CultureInfo.CurrentCulture);
        }
        else
        {
            BusiestDayFigureText = "–";
            BusiestDayDateText = "";
        }

        HasPreviousPeriod = figures.HasPreviousPeriod;
        VsPreviousCaption = figures.HasPreviousPeriod
            ? string.Format(CultureInfo.CurrentCulture, loc["Stats.VsPreviousValue"],
                StatsAggregator.ShortenTokenCount(previousTotal, thousand, million, billion))
            : loc["Stats.VsPrevious"];
        if (figures.HasPreviousPeriod)
        {
            // The one shared percentage-rounding helper (StatusTextMap.UsagePercent) rounds away
            // from zero regardless of sign, which is exactly the "+12%"/"-8%" behaviour this figure
            // wants too.
            var rounded = StatusTextMap.UsagePercent(figures.ChangePercent);
            ChangeFigureText = StatusTextMap.FormatPercent(rounded.ToString("+0;-0;+0", CultureInfo.CurrentCulture));
            ChangeIsIncrease = rounded >= 0;
        }
        else
        {
            ChangeFigureText = "–";
            ChangeIsIncrease = true;
        }
    }

    /// <summary>Every record from the period of the same length immediately before <paramref
    /// name="from"/> - empty for "All", which has no defined length to mirror.</summary>
    private static List<StatsRecord> PreviousPeriod(IReadOnlyList<StatsRecord> all, string range, DateOnly from, DateOnly today)
    {
        if (range == "All")
            return [];

        var periodDays = today.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(periodDays - 1));
        return all.Where(record => record.Day >= previousFrom && record.Day <= previousTo).ToList();
    }

    /// <summary>The first day a gap-filled by-day chart draws a bar for: the period's own start
    /// where it has one, and otherwise (the whole history, which has no lower bound) the earliest day
    /// actually stored. An empty store falls back to today, so the chart holds exactly one empty bar
    /// rather than a bar for every day since the year 1. Pure so it is unit testable without a
    /// store.</summary>
    internal static DateOnly GapFillStart(DateOnly from, IReadOnlyList<StatsRecord> inRange, DateOnly today)
    {
        if (from > DateOnly.MinValue)
            return from;

        return inRange.Count > 0 ? inRange.Min(record => record.Day) : today;
    }

    /// <summary>A raw effort level exactly as <see cref="StatsRecord.Effort"/> stores it (low, medium,
    /// high, xhigh, max, ...), except the empty level - a record whose own source line carried none -
    /// which becomes the localized "unknown" text. The level names themselves are never translated:
    /// they are the same handful of fixed English words Claude Code and Codex themselves use for this
    /// setting, not application text of this app's own choosing.</summary>
    private static string ResolveEffortLabel(string effort) =>
        string.IsNullOrEmpty(effort) ? LocalizationService.Instance["Stats.Effort.Unknown"] : effort;

    /// <summary>What <see cref="Views.Controls.StatsBarChart.Bar.ColorProviderId"/>/<see
    /// cref="Views.Controls.StatsBarChart.Bar.ColorRank"/> resolve into an actual color from, for the
    /// one grouping that produced this bar.</summary>
    private readonly record struct BarColorKey(string ProviderId, int Rank);

    /// <summary>Under project grouping the chart draws this many projects on their own bars and
    /// bundles the rest into one.</summary>
    private const int MaxProjectBars = 10;

    /// <summary>The fixed color key of the bundled "other projects" bar.</summary>
    internal const string OtherProjectsColorKey = "__other";

    /// <summary>One color key per row of <paramref name="groupedRows"/>, same order, same count.
    /// Day/Week stay ("", 0) - unused, since those bars keep the provider-stacked coloring <see
    /// cref="IsStackedByProvider"/> already drives. Effort keys on the raw effort level itself, the
    /// exact string <see cref="ChartPalette.ForProvider"/> already resolves the effort ring's own
    /// slices through. Project keys on the raw project folder, the key <see cref="ProjectColors"/> holds
    /// each project's own resolved color under. Model
    /// counts each model's rank within its own dominant provider across the FULL list, not just the
    /// ring's own top six - the same tie-break <see cref="StatsAggregator.ShareByModel"/> uses to pick
    /// that provider, and the same counting order <see cref="Views.StatsWindow"/>'s own ring-brush
    /// refresh uses, so a model inside the ring's top six always lands on the exact rank - and so the
    /// exact color - its slice already has.</summary>
    private static List<BarColorKey> BuildBarColorKeys(
        StatsGrouping grouping, IReadOnlyList<StatsGroupedRow> groupedRows, IReadOnlyList<StatsRecord> inRange)
    {
        if (grouping == StatsGrouping.Effort)
            return groupedRows.Select(row => new BarColorKey(row.Label, 0)).ToList();

        if (grouping == StatsGrouping.Project)
            return groupedRows.Select(row => new BarColorKey(row.Label, 0)).ToList();

        if (grouping == StatsGrouping.Model)
        {
            var providerByModel = inRange
                .GroupBy(record => ModelDisplayNames.Resolve(record.Model))
                .ToDictionary(
                    group => group.Key,
                    group => group.GroupBy(record => record.Provider)
                        .OrderByDescending(providerGroup => providerGroup.Sum(record => record.TotalTokens))
                        .First().Key);

            var rankByProvider = new Dictionary<string, int>();
            var keys = new List<BarColorKey>();
            foreach (var row in groupedRows)
            {
                var providerId = providerByModel.GetValueOrDefault(row.Label, "");
                var rank = rankByProvider.GetValueOrDefault(providerId);
                rankByProvider[providerId] = rank + 1;
                keys.Add(new BarColorKey(providerId, rank));
            }
            return keys;
        }

        return groupedRows.Select(_ => new BarColorKey("", 0)).ToList();
    }

    private DateOnly _monthGridToday;
    private DateOnly _monthGridPeriodStart;
    private DateOnly _monthGridPeriodEnd;
    private double _monthGridAvailableWidth;

    /// <summary>The width the grid has to lay its week columns in (the statistics window feeds its
    /// scroller's width here on every size change). A window that fits more weeks than the twelve
    /// months at the normal cell size makes the grid reach back that far; a change that leaves the
    /// start where it is does nothing, and none of it re-indexes or reloads the store.</summary>
    public double MonthGridAvailableWidth
    {
        get => _monthGridAvailableWidth;
        set
        {
            if (_monthGridAvailableWidth == value)
                return;
            _monthGridAvailableWidth = value;
            if (_recordsLoaded && DesiredMonthGridStart() != MonthGridRangeStart)
                RebuildMonthGrid(_allRecords, MonthGridColorScale);
        }
    }

    private DateOnly DesiredMonthGridStart()
    {
        var columns = Views.Controls.StatsMonthGrid.ColumnsFitting(_monthGridAvailableWidth);
        var weekStart = StatsAggregator.WeekStart(_monthGridToday);
        var widest = Math.Min(columns, Math.Max(1, (weekStart.DayNumber - DateOnly.MinValue.DayNumber) / 7));
        return StatsMonthGridBuilder.FirstDay(_monthGridToday, weekStart.AddDays(-(widest - 1) * 7));
    }

    private void RebuildMonthGrid(IReadOnlyList<StatsRecord> all, IReadOnlyList<long>? colorScale)
    {
        var monthGrid = StatsMonthGridBuilder.Build(
            all, _monthGridToday, from: DesiredMonthGridStart(), colorScale: colorScale);
        MonthGridDays = monthGrid.Days;
        MonthGridColorScale = monthGrid.ColorScaleTotals;
        MonthGridRangeStart = monthGrid.RangeStart;
        MonthGridRangeEnd = monthGrid.RangeEnd;
        MonthGridToday = _monthGridToday;
        var (highlightStart, highlightEnd) = MonthGridHighlight(
            _monthGridPeriodStart, _monthGridPeriodEnd, monthGrid.RangeStart, monthGrid.RangeEnd);
        MonthGridHighlightStart = highlightStart;
        MonthGridHighlightEnd = highlightEnd;
    }

    /// <summary>The part of the grid the selected period covers, or none when there is nothing to
    /// tell apart: the whole history (no lower bound) and a period reaching over the entire grid.</summary>
    internal static (DateOnly? Start, DateOnly? End) MonthGridHighlight(
        DateOnly periodStart, DateOnly periodEnd, DateOnly gridStart, DateOnly gridEnd)
    {
        if (periodStart <= gridStart && periodEnd >= gridEnd)
            return (null, null);
        return (periodStart, periodEnd);
    }

    /// <summary>Pure so the boundary logic is unit testable without a store - "All" has no lower
    /// bound at all, everything else is exactly <paramref name="today"/> minus the period's day
    /// count.</summary>
    internal static DateOnly RangeStart(string range, DateOnly today) => range switch
    {
        "Week" => today.AddDays(-6),
        "Month" => today.AddDays(-29),
        "Year" => today.AddMonths(-12).AddDays(1),
        _ => DateOnly.MinValue,
    };

    /// <summary>Writes exactly the table currently shown - same rows, same order - to a CSV file the
    /// user picks, reusing <see cref="StatsExport"/>'s writer. Cancelling the dialog does
    /// nothing; a write failure shows the app's own error text, never a raw exception message.</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var path = _askForSavePath($"ai-usage-stats-{DateTime.Now:yyyy-MM-dd}.csv");
        if (path is null)
            return;

        var loc = LocalizationService.Instance;
        var labelHeader = GroupingChoices.First(choice => choice.Value == SelectedGrouping).Label;
        var csv = StatsExport.ToCsv(Rows, labelHeader, loc["StatsWindow.ColumnTotal"]);

        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(loc["StatsWindow.ExportFailed"]);
        }
    }

    private static string? ShowRealSaveDialog(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
