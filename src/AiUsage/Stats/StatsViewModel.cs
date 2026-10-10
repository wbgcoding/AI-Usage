using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.Stats;

/// <summary>What the segments of a day or week column stand for.</summary>
public enum StatsColorBy
{
    Provider,
    Model,
    Agent,
}

/// <summary>One stacked series of the chart: its name and the key its color is looked up by (a
/// provider id, <c>cat:N</c> for the N-th categorical color, or <c>other</c> for the pooled
/// remainder).</summary>
public sealed record ChartSeriesInfo(string Label, string ColorKey);

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
    private readonly HistoryStore _history;
    private readonly Func<string, string?> _askForSavePath;
    private readonly Action<string> _showMessage;

    public ObservableCollection<Choice<string>> RangeChoices { get; } = [];
    public ObservableCollection<Choice<StatsGrouping>> GroupingChoices { get; } = [];
    public ObservableCollection<Choice<StatsColorBy>> ColorByChoices { get; } = [];
    public ObservableCollection<Choice<string>> ProviderChoices { get; } = [];

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
            if (value is null)
                return;
            // The custom range is not a period yet: picking it asks for the two dates first.
            if (value.Value == CustomRange)
                RequestCustomRange();
            else
                SetRangeCommand.Execute(value.Value);
        }
    }

    /// <summary>The range value that stands for the two dates the user picked.</summary>
    internal const string CustomRange = "Custom";

    /// <summary>Raised when the custom range choice wants its date popup shown.</summary>
    public event EventHandler? CustomRangeRequested;

    /// <summary>Raised once a custom range was applied, so the popup can close.</summary>
    public event EventHandler? CustomRangeApplied;

    /// <summary>The first and last day of the applied custom range.</summary>
    private DateOnly _customFrom;
    private DateOnly _customTo;

    /// <summary>The day the date popup's "from" picker shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomRangeCommand))]
    private DateTime? customFromDate;

    /// <summary>The day the date popup's "to" picker shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomRangeCommand))]
    private DateTime? customToDate;

    /// <summary>Fills the popup's pickers (the applied range, or the last 30 days) and asks the window
    /// to show it.</summary>
    internal void RequestCustomRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = SelectedRange == CustomRange ? _customFrom : RangeStart("Month", today);
        var to = SelectedRange == CustomRange ? _customTo : today;
        CustomFromDate = from.ToDateTime(TimeOnly.MinValue);
        CustomToDate = to.ToDateTime(TimeOnly.MinValue);
        CustomRangeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The applied custom range, for saving it.</summary>
    internal (DateOnly From, DateOnly To) CustomPeriod => (_customFrom, _customTo);

    /// <summary>Takes up what the window was left on. Anything unknown (a hand-edited or older
    /// settings file) keeps the default; a custom range without both dates counts as unknown. The
    /// records are read once the window loads, so nothing is read here.</summary>
    public void RestoreSelection(
        string? range, string? grouping, string? colorBy, DateOnly? customFrom, DateOnly? customTo, string? provider = null)
    {
        if (customFrom is { } first && customTo is { } last)
        {
            _customFrom = first <= last ? first : last;
            _customTo = first <= last ? last : first;
        }

        if (RangeChoices.Any(choice => choice.Value == range) && (range != CustomRange || (customFrom is not null && customTo is not null)))
            SelectedRange = range!;
        if (Enum.TryParse<StatsGrouping>(grouping, out var parsedGrouping) && Enum.IsDefined(parsedGrouping))
            SelectedGrouping = parsedGrouping;
        if (Enum.TryParse<StatsColorBy>(colorBy, out var parsedColorBy) && Enum.IsDefined(parsedColorBy))
            SelectedColorBy = parsedColorBy;

        if (provider is not null && ProviderChoices.Any(choice => choice.Value == provider))
            SelectedProvider = provider;

        UpdateCustomChoiceLabel();
        Choice.Select(RangeChoices, SelectedRange);
        Choice.Select(GroupingChoices, SelectedGrouping);
        Choice.Select(ColorByChoices, SelectedColorBy);
        Choice.Select(ProviderChoices, SelectedProvider);
        OnPropertyChanged(nameof(SelectedProviderChoice));
        OnPropertyChanged(nameof(SelectedRangeChoice));
        OnPropertyChanged(nameof(SelectedGroupingChoice));
        OnPropertyChanged(nameof(SelectedColorByChoice));
        OnPropertyChanged(nameof(IsStackedByProvider));
        OnPropertyChanged(nameof(IsDayGrouping));
        if (_recordsLoaded)
            RecomputeFrom(_sourceRecords);
    }

    /// <summary>Puts the combo back on the range that is really applied - after the date popup was
    /// closed without applying anything.</summary>
    public void ResyncRangeChoice()
    {
        Choice.Select(RangeChoices, SelectedRange);
        OnPropertyChanged(nameof(SelectedRangeChoice));
    }

    private bool CanApplyCustomRange() => CustomFromDate is not null && CustomToDate is not null;

    [RelayCommand(CanExecute = nameof(CanApplyCustomRange))]
    private async Task ApplyCustomRange()
    {
        if (CustomFromDate is not { } from || CustomToDate is not { } to)
            return;

        var first = DateOnly.FromDateTime(from);
        var last = DateOnly.FromDateTime(to);
        await SetCustomRange(first <= last ? first : last, first <= last ? last : first);
        CustomRangeApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a custom range of the two days, whichever way round they come.</summary>
    internal async Task SetCustomRange(DateOnly from, DateOnly to)
    {
        _customFrom = from;
        _customTo = to;
        SelectedDay = null;
        SelectedRange = CustomRange;
        UpdateCustomChoiceLabel();
        ResyncRangeChoice();
        await RecomputeForSelectionAsync();
    }

    /// <summary>The custom choice names its dates while it is the applied range, and the plain
    /// "custom range" wording otherwise.</summary>
    private void UpdateCustomChoiceLabel()
    {
        var custom = RangeChoices.FirstOrDefault(choice => choice.Value == CustomRange);
        if (custom is null)
            return;

        custom.Label = SelectedRange == CustomRange
            ? string.Format(
                CultureInfo.CurrentCulture, "{0:d} - {1:d}",
                _customFrom.ToDateTime(TimeOnly.MinValue), _customTo.ToDateTime(TimeOnly.MinValue))
            : LocalizationService.Instance["Stats.Range.Custom"];
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

    /// <summary>Same bridge as <see cref="SelectedRangeChoice"/>, for the provider filter.</summary>
    public Choice<string>? SelectedProviderChoice
    {
        get => ProviderChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is not null)
                SetProviderCommand.Execute(value.Value);
        }
    }

    /// <summary>Same bridge as <see cref="SelectedRangeChoice"/>, for the color-by group.</summary>
    public Choice<StatsColorBy>? SelectedColorByChoice
    {
        get => ColorByChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is not null)
                SetColorByCommand.Execute(value.Value);
        }
    }

    [ObservableProperty]
    private string selectedRange = "Week";

    /// <summary>The one provider every figure is limited to; empty for all providers.</summary>
    [ObservableProperty]
    private string selectedProvider = "";

    /// <summary>The model (its display name) a click narrowed every figure to; empty for none. Not
    /// remembered between sessions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelFilter), nameof(ModelFilterText), nameof(HasAnyFilter))]
    private string selectedModelFilter = "";

    /// <summary>The project (its grouping key) a click narrowed every figure to; empty for none. Not
    /// remembered between sessions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectFilter), nameof(ProjectFilterText), nameof(HasAnyFilter))]
    private string selectedProjectFilter = "";

    public bool HasModelFilter => SelectedModelFilter.Length > 0;

    public bool HasProjectFilter => SelectedProjectFilter.Length > 0;

    public bool HasAnyFilter => HasModelFilter || HasProjectFilter;

    /// <summary>The filter chip's text, for example "Model: Opus 4.5".</summary>
    public string ModelFilterText => LocalizationService.Instance.Format("Stats.Filter.Model", SelectedModelFilter);

    public string ProjectFilterText => LocalizationService.Instance.Format(
        "Stats.Filter.Project",
        StatsAggregator.MiddleEllipsis(StatsAggregator.ShortenProjectLabel(SelectedProjectFilter), StatsAggregator.ProjectLabelMaxChars));

    /// <summary>True while the breakdown chart draws one bar per model or project, so a click on a bar
    /// can filter.</summary>
    [ObservableProperty]
    private bool barsFilterable;

    // What each drawn bar of the breakdown chart narrows to when clicked; null for a bar that names
    // neither a model nor a project (the pooled "other projects" bar).
    private List<(StatsFilterKind Kind, string Key)?> _barTargets = [];

    /// <summary>What the segments of a day or week column stand for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelStack), nameof(IsAgentStack))]
    private StatsColorBy selectedColorBy = StatsColorBy.Provider;

    /// <summary>The series of the stacked chart, in stacking order; empty for the groupings that draw
    /// one plain series.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChartSeriesInfo> chartSeries = [];

    /// <summary>The trailing 7-day mean drawn as a line across the day columns, one entry per
    /// column (the first six empty); empty unless the day grouping spans enough days.</summary>
    [ObservableProperty]
    private IReadOnlyList<double?> chartOverlay = [];

    /// <summary>The shortest day range the average line is drawn for.</summary>
    private const int OverlayMinDays = 14;

    /// <summary>True for the groupings whose columns can be stacked by provider or by model.</summary>
    public bool CanChooseColor => IsStackedByProvider;

    /// <summary>True while the stacked columns are split by model.</summary>
    public bool IsModelStack => CanChooseColor && SelectedColorBy == StatsColorBy.Model;

    /// <summary>True while the stacked columns are split into main agent and subagents.</summary>
    public bool IsAgentStack => CanChooseColor && SelectedColorBy == StatsColorBy.Agent;

    /// <summary>The most models a stacked column names on its own; the rest are pooled.</summary>
    private const int MaxStackedModels = 6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartMaxAxisLabels), nameof(IsHourAxis), nameof(ChartTooltip), nameof(IsHeatmap), nameof(IsBarChart), nameof(CanChooseColor), nameof(IsModelStack), nameof(IsAgentStack), nameof(IsSessionGrouping), nameof(ShowPlainTable))]
    private StatsGrouping selectedGrouping = StatsGrouping.Day;

    /// <summary>True when the day grouping draws one column per calendar week instead of one per
    /// day: only for a whole history that spans more than a year. Up to 366 days every day gets its
    /// own column, the 12 month range included. The table under the day grouping stays per day
    /// regardless.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartTooltip))]
    private bool isWeeklyPerDay;

    private const int WeeklyPerDayThresholdDays = 366;

    /// <summary>The 24 hours pack into the width the other groupings draw 7-30 columns into, so the
    /// hour grouping keeps a tighter label spacing; every other grouping keeps the control's own
    /// default.</summary>
    public int ChartMaxAxisLabels => SelectedGrouping == StatsGrouping.Hour ? 8 : 10;

    /// <summary>True while the chart draws the 24 hours, which label their axis at fixed hours
    /// instead of by width.</summary>
    public bool IsHourAxis => SelectedGrouping == StatsGrouping.Hour;

    /// <summary>The breakdown section's header tooltip, following the grouping: what one column
    /// stands for. Empty for the groupings whose columns need no explanation.</summary>
    public string ChartTooltip => SelectedGrouping switch
    {
        StatsGrouping.Day => LocalizationService.Instance[IsWeeklyPerDay ? "Tip.Stats.Chart.PerWeek" : "Tip.Stats.Chart.PerDay"],
        StatsGrouping.Week => LocalizationService.Instance["Tip.Stats.Chart.PerWeek"],
        StatsGrouping.Weekday => LocalizationService.Instance["Tip.Stats.Chart.ByWeekday"],
        StatsGrouping.Hour => LocalizationService.Instance["Tip.Stats.Chart.ByHour"],
        StatsGrouping.WeekdayHour => LocalizationService.Instance["Tip.Stats.Chart.ByWeekdayHour"],
        _ => "",
    };

    /// <summary>True while the chart area shows the weekday by hour grid instead of columns.</summary>
    public bool IsHeatmap => SelectedGrouping == StatsGrouping.WeekdayHour;

    /// <summary>True while the chart area shows columns (the session grouping draws horizontal bars instead).</summary>
    public bool IsBarChart => !IsHeatmap && !IsSessionGrouping;

    /// <summary>True while the breakdown lists sessions: the largest as horizontal bars, all of them in the table.</summary>
    public bool IsSessionGrouping => SelectedGrouping == StatsGrouping.Session;

    /// <summary>True for every grouping whose table is the plain label and total list.</summary>
    public bool ShowPlainTable => !IsSessionGrouping;

    /// <summary>The most sessions the bars name; the table lists all of them.</summary>
    private const int MaxSessionBars = 20;

    /// <summary>The largest sessions of the range, as bars.</summary>
    [ObservableProperty]
    private IReadOnlyList<StatsProjectRow> sessionChartRows = [];

    /// <summary>Every session of the range, as table rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<StatsSessionRowViewModel> sessionRows = [];

    /// <summary>The weekday by hour totals, 168 values row by row, the first row being <see
    /// cref="HeatmapFirstDay"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<long> heatmapValues = [];

    /// <summary>The culture's first day of the week, the weekday of the grid's first row.</summary>
    [ObservableProperty]
    private DayOfWeek heatmapFirstDay = DayOfWeek.Monday;

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

    /// <summary>The figure cards' column count: four, plus the "per active day" card and the subagent
    /// share card whenever they are shown.</summary>
    public int FigureCardColumns => 4 + (ShowPerActiveDay ? 1 : 0) + (HasSubagentShare ? 1 : 0);

    /// <summary>False when the range holds no Claude tokens: the subagent share card is then left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FigureCardColumns))]
    private bool hasSubagentShare;

    /// <summary>The subagents' share of the Claude tokens in the range, as a percentage text.</summary>
    [ObservableProperty]
    private string subagentShareText = "";

    [ObservableProperty]
    private string subagentShareCaption = "";

    /// <summary>The raw numbers behind the share, for the card's progress bar.</summary>
    [ObservableProperty]
    private double subagentTokensRaw;

    [ObservableProperty]
    private double claudeTokensRaw;

    [ObservableProperty]
    private string activeDaysOfText = "";

    [ObservableProperty]
    private IReadOnlyList<Views.Controls.StatsBarChart.Bar> bars = [];

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

    [ObservableProperty]
    private IReadOnlyList<StatsRowViewModel> rows = [];

    [ObservableProperty]
    private IReadOnlyList<StatsProviderRowViewModel> providerRows = [];

    /// <summary>The limits section: the quota windows of each shown provider, with the tokens-per-percent
    /// line under the provider's rows where there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LimitRows), nameof(HasLimitRows), nameof(HasNoLimitRows))]
    private IReadOnlyList<StatsLimitGroupViewModel> limitGroups = [];

    /// <summary>Every row of <see cref="LimitGroups"/> in order.</summary>
    public IReadOnlyList<StatsLimitRowViewModel> LimitRows => LimitGroups.SelectMany(group => group.Rows).ToList();

    public bool HasLimitRows => LimitGroups.Count > 0;

    public bool HasNoLimitRows => LimitGroups.Count == 0;

    /// <summary>How many accounts a provider has - a seam the window fills from the settings. The
    /// tokens-per-percent estimate needs exactly one: usage of several accounts is mixed in the logs.</summary>
    internal Func<string, int> AccountCountOf { get; set; } = _ => 1;

    private IReadOnlyList<StatsRecord>? _perPercentSource;
    private readonly Dictionary<string, PerPercentResult> _perPercent = [];

    /// <summary>Reads one provider's quota history between two moments - a seam so a test can supply points.</summary>
    internal Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>> LoadHistory { get; set; }

    /// <summary>The names of the other PCs whose token history was imported into the index; set by the
    /// window that owns the settings, empty for a view model that has none.</summary>
    internal Func<IReadOnlyList<string>> ImportedMachineNames { get; set; } = () => [];

    /// <summary>The muted line under the selectors naming the PCs whose history is in the figures.</summary>
    public string ImportedFromText => ImportedMachineNames() is { Count: > 0 } names
        ? LocalizationService.Instance.Format("Stats.ImportedFrom", string.Join(", ", names))
        : "";

    public bool HasImportedFrom => ImportedFromText.Length > 0;

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

    /// <summary>Every stored record before the filters; <see cref="_allRecords"/> is what they let through.</summary>
    private IReadOnlyList<StatsRecord> _sourceRecords = [];

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

    public StatsViewModel(
        StatsStore store, Func<string, string?>? askForSavePath = null, Action<string>? showMessage = null, HistoryStore? history = null)
    {
        _store = store;
        _history = history ?? (store.FixedDirectory is { } directory ? new HistoryStore(directory, now: null) : new HistoryStore());
        LoadHistory = (providerId, from, to) => _history.Load(providerId, from, to);
        LoadRecords = () => StatsAggregator.ResolveBareProjectNames(_store.LoadAll());
        LoadSessions = _store.LoadSessions;
        _askForSavePath = askForSavePath ?? ShowRealSaveDialog;
        _showMessage = showMessage ?? (message => Views.ConfirmWindow.ShowInfo(null, message));

        RangeChoices.Add(new Choice<string>("Chart.Range.Week", "Week"));
        RangeChoices.Add(new Choice<string>("Chart.Range.Month", "Month"));
        RangeChoices.Add(new Choice<string>("Stats.Range.ThisMonth", "ThisMonth"));
        RangeChoices.Add(new Choice<string>("Stats.Range.LastMonth", "LastMonth"));
        RangeChoices.Add(new Choice<string>("StatsWindow.Range.TwelveMonths", "Year"));
        RangeChoices.Add(new Choice<string>("Chart.Range.All", "All"));
        RangeChoices.Add(new Choice<string>("Stats.Range.Custom", CustomRange));
        Choice.Select(RangeChoices, SelectedRange);

        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByDay", StatsGrouping.Day));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByWeek", StatsGrouping.Week));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByWeekday", StatsGrouping.Weekday));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByHour", StatsGrouping.Hour));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByWeekdayHour", StatsGrouping.WeekdayHour));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByModel", StatsGrouping.Model));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByProject", StatsGrouping.Project));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.BySession", StatsGrouping.Session));
        GroupingChoices.Add(new Choice<StatsGrouping>("Stats.ByEffort", StatsGrouping.Effort));
        Choice.Select(GroupingChoices, SelectedGrouping);

        ColorByChoices.Add(new Choice<StatsColorBy>("Stats.ColorBy.Provider", StatsColorBy.Provider));
        ColorByChoices.Add(new Choice<StatsColorBy>("Stats.ColorBy.Model", StatsColorBy.Model));
        ColorByChoices.Add(new Choice<StatsColorBy>("Stats.ColorBy.Agent", StatsColorBy.Agent));
        Choice.Select(ColorByChoices, SelectedColorBy);

        ProviderChoices.Add(new Choice<string>("Stats.Filter.AllProviders", ""));
        foreach (var id in ProviderCoverage.IndexedProviderIds)
            ProviderChoices.Add(Choice.WithFixedLabel(ProviderDisplayNames.GetValueOrDefault(id, id), id));
        Choice.Select(ProviderChoices, SelectedProvider);

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
        UpdateCustomChoiceLabel();
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

    /// <summary>Switches what the stacked columns stand for. The records are already in memory, so
    /// the chart is rebuilt from them without another read.</summary>
    [RelayCommand]
    private void SetColorBy(StatsColorBy colorBy)
    {
        if (colorBy == SelectedColorBy)
            return;
        SelectedColorBy = colorBy;
        Choice.Select(ColorByChoices, SelectedColorBy);
        OnPropertyChanged(nameof(SelectedColorByChoice));
        if (_recordsLoaded)
            RecomputeFrom(_sourceRecords);
    }

    /// <summary>Limits every figure to one provider, or back to all of them with an empty id. The
    /// records are already in memory, so everything is rebuilt from them.</summary>
    [RelayCommand]
    private void SetProvider(string provider)
    {
        if (provider == SelectedProvider)
            return;
        SelectedProvider = provider;
        Choice.Select(ProviderChoices, SelectedProvider);
        OnPropertyChanged(nameof(SelectedProviderChoice));
        if (_recordsLoaded)
            RecomputeFrom(_sourceRecords);
    }

    /// <summary>Keeps what the active filters let through.</summary>
    private IReadOnlyList<StatsRecord> ApplyFilters(IReadOnlyList<StatsRecord> all)
    {
        if (SelectedProvider.Length == 0 && !HasAnyFilter)
            return all;

        var noProject = LocalizationService.Instance["Stats.NoProject"];
        return all.Where(record =>
                (SelectedProvider.Length == 0 || record.Provider == SelectedProvider)
                && (!HasModelFilter || ModelDisplayNames.Resolve(record.Model) == SelectedModelFilter)
                && (!HasProjectFilter || MatchesProject(record.Project, noProject)))
            .ToList();
    }

    private IReadOnlyList<StatsSessionRecord> ApplyFilters(IReadOnlyList<StatsSessionRecord> all)
    {
        if (SelectedProvider.Length == 0 && !HasAnyFilter)
            return all;

        var noProject = LocalizationService.Instance["Stats.NoProject"];
        return all.Where(session =>
                (SelectedProvider.Length == 0 || session.Provider == SelectedProvider)
                && (!HasModelFilter || ModelDisplayNames.Resolve(session.MainModel) == SelectedModelFilter)
                && (!HasProjectFilter || MatchesProject(session.Project, noProject)))
            .ToList();
    }

    private bool MatchesProject(string project, string noProjectLabel) => string.Equals(
        string.IsNullOrEmpty(project) ? noProjectLabel : StatsAggregator.ProjectKey(project),
        SelectedProjectFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>A click on a model or a project: narrows every figure to it, or lifts that filter
    /// when it is already the one applied.</summary>
    public void ToggleFilter(StatsFilterKind kind, string key)
    {
        if (key.Length == 0)
            return;

        switch (kind)
        {
            case StatsFilterKind.Model:
                SelectedModelFilter = SelectedModelFilter == key ? "" : key;
                break;
            case StatsFilterKind.Project:
                SelectedProjectFilter = string.Equals(SelectedProjectFilter, key, StringComparison.OrdinalIgnoreCase) ? "" : key;
                break;
            default:
                return;
        }

        if (_recordsLoaded)
            RecomputeFrom(_sourceRecords);
    }

    /// <summary>A click on bar <paramref name="index"/> of the breakdown chart.</summary>
    public void ActivateBar(int index)
    {
        if (index >= 0 && index < _barTargets.Count && _barTargets[index] is { } target)
            ToggleFilter(target.Kind, target.Key);
    }

    /// <summary>A click on slice <paramref name="index"/> of the model ring; the pooled "other" slice
    /// names no model and does nothing.</summary>
    public void ActivateModelSlice(int index)
    {
        if (index < 0 || index >= ModelShareSlices.Count)
            return;

        var slice = ModelShareSlices[index];
        if (slice.Label != LocalizationService.Instance["Stats.Other"])
            ToggleFilter(StatsFilterKind.Model, slice.Label);
    }

    /// <summary>A click on row <paramref name="index"/> of the top projects panel.</summary>
    public void ActivateTopProject(int index)
    {
        if (index >= 0 && index < TopProjectRows.Count)
            ToggleFilter(StatsFilterKind.Project, TopProjectRows[index].FullPath);
    }

    [RelayCommand]
    private void ClearModelFilter() => ToggleFilter(StatsFilterKind.Model, SelectedModelFilter);

    [RelayCommand]
    private void ClearProjectFilter() => ToggleFilter(StatsFilterKind.Project, SelectedProjectFilter);

    // Every run takes the next number; only the newest run may publish its result or clear
    // IsLoading, so two overlapping runs can never leave the older one's data on screen.
    private int _recomputeGeneration;

    /// <summary>How long a run must take before <see cref="IsLoading"/> turns on, so a quick rebuild
    /// never flashes the "loading" state.</summary>
    internal TimeSpan LoadingDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Reads and resolves every stored record - a seam so a test can slow a run down.</summary>
    internal Func<IReadOnlyList<StatsRecord>> LoadRecords { get; set; }

    /// <summary>Reads every stored session - a seam like <see cref="LoadRecords"/>.</summary>
    internal Func<IReadOnlyList<StatsSessionRecord>> LoadSessions { get; set; }

    /// <summary>Every stored session, kept from the last load for the session grouping.</summary>
    private IReadOnlyList<StatsSessionRecord> _allSessions = [];

    /// <summary>A period or grouping pick: the records are already in memory once a load has finished
    /// (only a window open or an index update changes them), so the rebuild reuses them instead of
    /// reading the whole store again. Before that first load it is the normal read.</summary>
    private Task RecomputeForSelectionAsync()
    {
        if (!_recordsLoaded)
            return RecomputeAsync();

        RecomputeFrom(_sourceRecords);
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
            var (all, sessions) = await Task.Run(() => (LoadRecords(), LoadSessions()));
            if (generation == _recomputeGeneration)
            {
                _allSessions = sessions;
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
        _allSessions = LoadSessions();
        RecomputeFrom(LoadRecords());
        _recordsLoaded = true;
        IsLoading = false;
    }

    /// <summary>The rebuild itself, on records already read - the one half <see
    /// cref="RecomputeAsync"/> keeps on the window's own thread.</summary>
    private void RecomputeFrom(IReadOnlyList<StatsRecord> source)
    {
        _sourceRecords = source;
        OnPropertyChanged(nameof(ImportedFromText));
        OnPropertyChanged(nameof(HasImportedFrom));
        var all = ApplyFilters(source);
        // Days are bucketed in local time, so "today" is the local calendar day too.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = ResolvePeriod(SelectedRange, today, _customFrom, _customTo);
        var from = period.From;
        var to = period.To;

        var inRange = all.Where(record => record.Day >= from && record.Day <= to).ToList();
        var previous = PreviousPeriod(all, period);
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
        var gapFillFrom = GapFillStart(from, inRange, to);

        var weeklyPerDay = SelectedRange == "All" && to.DayNumber - gapFillFrom.DayNumber + 1 > WeeklyPerDayThresholdDays;
        IsWeeklyPerDay = weeklyPerDay;

        // The table under the day grouping stays per day; only the bars are bundled per week.
        var dayRows = SelectedGrouping == StatsGrouping.Day
            ? StatsAggregator.Group(inRange, StatsGrouping.Day, gapFillFrom, to)
            : null;
        var weekRows = weeklyPerDay ? StatsAggregator.Group(inRange, StatsGrouping.Week, gapFillFrom, to) : null;

        // Week grouping keeps its empty weeks as zero rows, the same way the day bars keep empty days.
        var groupedRows = SelectedGrouping switch
        {
            StatsGrouping.Day => weekRows ?? dayRows!,
            StatsGrouping.Week => weekRows ?? StatsAggregator.Group(inRange, StatsGrouping.Week, gapFillFrom, to),
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

        // Day and week columns are stacked by provider or, on request, by model.
        var series = new List<ChartSeriesInfo>();
        if (IsStackedByProvider && !IsModelStack && !IsAgentStack)
        {
            series.AddRange(StatsAggregator.StackedProviderOrder.Select(id =>
                new ChartSeriesInfo(ProviderDisplayNames.GetValueOrDefault(id, id), id)));
        }
        else if (IsModelStack)
        {
            var stackGrouping = SelectedGrouping == StatsGrouping.Week || weeklyPerDay ? StatsGrouping.Week : StatsGrouping.Day;
            var stack = StatsAggregator.GroupStackedByModel(
                inRange, stackGrouping, gapFillFrom, to, MaxStackedModels, LocalizationService.Instance["Stats.Other"]);
            barRows = stack.Rows;
            series.AddRange(stack.Series.Select((name, index) =>
                new ChartSeriesInfo(name, stack.HasOther && index == stack.Series.Count - 1 ? "other" : "cat:" + index)));
        }
        else if (IsAgentStack)
        {
            var stackGrouping = SelectedGrouping == StatsGrouping.Week || weeklyPerDay ? StatsGrouping.Week : StatsGrouping.Day;
            var mainLabel = LocalizationService.Instance["Stats.Agent.Main"];
            var subagentLabel = LocalizationService.Instance["Stats.SubagentShare"];
            barRows = StatsAggregator.GroupStackedByAgent(inRange, stackGrouping, gapFillFrom, to, mainLabel, subagentLabel).Rows;
            series.Add(new ChartSeriesInfo(mainLabel, "cat:0"));
            series.Add(new ChartSeriesInfo(subagentLabel, "cat:1"));
        }
        ChartSeries = series;
        ChartOverlay = SelectedGrouping == StatsGrouping.Day && !weeklyPerDay && barRows.Count >= OverlayMinDays
            ? StatsAggregator.TrailingMean(barRows.Select(row => row.Total).ToList(), 7)
            : [];

        var filterKind = SelectedGrouping switch
        {
            StatsGrouping.Model => StatsFilterKind.Model,
            StatsGrouping.Project => StatsFilterKind.Project,
            _ => StatsFilterKind.None,
        };
        var otherProjectsLabel = LocalizationService.Instance["Stats.OtherProjects"];
        BarsFilterable = filterKind != StatsFilterKind.None && !IsModelStack && !IsAgentStack;
        _barTargets = BarsFilterable
            ? barRows.Select(row => row.Label == otherProjectsLabel && filterKind == StatsFilterKind.Project
                ? ((StatsFilterKind Kind, string Key)?)null
                : (filterKind, row.Label)).ToList()
            : [];

        Bars = barRows.Select((row, index) => new Views.Controls.StatsBarChart.Bar(
            row.Label, row.StackedValues, barKeys[index].ProviderId, barKeys[index].Rank,
            barKeys[index].ProviderId == OtherProjectsColorKey ? otherTooltipLine : null)).ToList();

        // The month grid's own by-day list: the last twelve months up to today (local time), or further
        // back for a wide window (see MonthGridAvailableWidth), never the selected range or grouping -
        // stacked by all five providers rather than just the two
        // StackedProviderOrder names, since any of them can lead a day on this grid. Built by
        // StatsMonthGridBuilder, the one place this computation lives - the widget's own day-grid tile
        // (DayGridTileViewModel) reads the same store through the same builder, so the two never
        // disagree about a day's color or total.
        _monthGridToday = DateOnly.FromDateTime(DateTime.Now);
        _monthGridPeriodStart = from;
        _monthGridPeriodEnd = to;
        RebuildMonthGrid(all, colorScale: null);

        Rows = (dayRows ?? groupedRows)
            .Select(row =>
            {
                var totalText = row.Total.ToString("N0", CultureInfo.CurrentCulture);
                if (SelectedGrouping == StatsGrouping.Model)
                    return new StatsRowViewModel(row.Label, row.Total, totalText, FilterKind: StatsFilterKind.Model, FilterKey: row.Label);
                if (SelectedGrouping != StatsGrouping.Project)
                    return new StatsRowViewModel(row.Label, row.Total, totalText);

                var shortened = StatsAggregator.MiddleEllipsis(StatsAggregator.ShortenProjectLabel(row.Label), StatsAggregator.ProjectLabelMaxChars);
                return new StatsRowViewModel(shortened, row.Total, totalText, ToolTip: row.Label, FilterKind: StatsFilterKind.Project, FilterKey: row.Label);
            })
            .ToList();

        var summary = StatsAggregator.Summarize(inRange, previous);
        var loc = LocalizationService.Instance;

        if (SelectedGrouping == StatsGrouping.Session)
            BuildSessionViews(from, to, loc);
        else
        {
            SessionChartRows = [];
            SessionRows = [];
        }

        // A provider without local token counts (Cursor, Copilot) is dropped
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

        LimitGroups = BuildLimitGroups(from, to, loc);

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

        // The subagent share card: left out while the range holds no Claude tokens.
        var subagentShare = StatsAggregator.SubagentShare(inRange);
        HasSubagentShare = subagentShare.HasClaudeTokens;
        SubagentTokensRaw = subagentShare.SubagentTokens;
        ClaudeTokensRaw = subagentShare.ClaudeTokens;
        SubagentShareText = subagentShare.HasClaudeTokens
            ? StatusTextMap.FormatPercent(StatusTextMap.UsagePercent(subagentShare.Percent).ToString(CultureInfo.CurrentCulture))
            : "";
        SubagentShareCaption = loc["Stats.SubagentShare.Caption"];

        // The cache-share bar.
        var cacheShare = StatsAggregator.CacheShare(inRange);
        CacheShareValues = cacheShare.AsValues;
        CacheShareLabels = [loc["Stats.Cache.New"], loc["Stats.Cache.Write"], loc["Stats.Cache.Read"], loc["Stats.Cache.Output"]];

        // The "top projects" panel.
        TopProjectRows = StatsAggregator.TopProjectsDetailed(inRange, limit: 12, loc["Stats.NoProject"]);
        RefreshProjectColors(TopProjectRows, CurrentBarProjectKeys());

        // The weekday by hour grid, in the culture's own week order.
        HeatmapFirstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        HeatmapValues = StatsAggregator.FlattenWeekdayHour(StatsAggregator.GroupByWeekdayHour(inRange), HeatmapFirstDay);

        var periodDayCount = PeriodDayCount(SelectedRange, from, to, all);
        // The preceding period always has the range's fixed length, unlike periodDayCount, which a
        // short history can cut down.
        var figures = StatsAggregator.ComputeHeadlineFigures(
            inRange, previous, periodDayCount, period.HasPrevious ? period.PreviousTo.DayNumber - period.PreviousFrom.DayNumber + 1 : 0);
        ApplyHeadlineFigures(figures, previous.Sum(record => record.TotalTokens), loc);

        // The figures bar's mini charts - see the raw properties' own doc comments.
        // Always the period's own last periodDayCount days: "from" is DateOnly.MinValue for the whole
        // history, which would put every record outside the series.
        DailyTotalsSeries = StatsAggregator.DailyTotalsSeries(inRange, to.AddDays(-(Math.Max(1, periodDayCount) - 1)), periodDayCount);
        PeriodTotalRaw = figures.Total;
        PreviousPeriodTotalRaw = previous.Sum(record => record.TotalTokens);
        ActiveDayCountRaw = figures.ActiveDayCount;
        PeriodDayCountRaw = Math.Max(1, periodDayCount);
        ActiveDaysOfText = string.Format(CultureInfo.CurrentCulture, loc["Stats.ActiveDaysOf"], figures.ActiveDayCount, periodDayCount);
    }

    /// <summary>The limits section: the quota history of the shown providers (the filtered one, else
    /// every provider) between the first and last day of the range, summed per window, grouped by
    /// provider.</summary>
    private List<StatsLimitGroupViewModel> BuildLimitGroups(DateOnly from, DateOnly to, LocalizationService loc)
    {
        var start = from == DateOnly.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue));
        IEnumerable<string> providerIds = SelectedProvider.Length > 0 ? [SelectedProvider] : ProviderCoverage.AllProviderIds;
        var series = providerIds.Select(id => (id, LoadHistory(id, start, end))).ToList();
        return StatsLimitsAggregator.Build(series)
            .GroupBy(row => row.ProviderId)
            .Select(group =>
            {
                var rows = group.Select(row => new StatsLimitRowViewModel(
                    $"{ProviderDisplayNames.GetValueOrDefault(row.ProviderId, row.ProviderId)} · {StatusTextMap.Resolve(row.WindowLabelKey)}",
                    string.Format(CultureInfo.CurrentCulture, loc["Stats.Limits.ReachedCount"], row.ReachedCount),
                    FormatLimitPercent(row.Highest),
                    FormatLimitPercent(row.AveragePeak))).ToList();
                var (note, tip) = PerPercentNote(group.Key, loc);
                return new StatsLimitGroupViewModel(group.Key, rows, note, tip);
            })
            .ToList();
    }

    /// <summary>The "1 % of the week is about ..." line of a provider and its tooltip; empty for a
    /// provider the estimate does not cover.</summary>
    private (string Text, string ToolTip) PerPercentNote(string providerId, LocalizationService loc)
    {
        if (providerId is not (StatsIndexer.ClaudeProviderId or StatsIndexer.CodexProviderId))
            return ("", "");
        if (AccountCountOf(providerId) > 1)
            return (loc["Stats.PerPercent.MultiAccount"], "");

        var result = EstimatePerPercent(providerId);
        switch (result.Status)
        {
            case PerPercentStatus.PerModel:
                var items = string.Join(" · ", result.Items.Select(item => loc.Format(
                    "Stats.PerPercent.Item", ShortenTokens(item.TokensPerPercent, loc), item.IsOther ? loc["Stats.Other"] : item.Model)));
                var cached = result.CacheReadPerPercent > 0 ? ShortenTokens(result.CacheReadPerPercent, loc) : "–";
                return (loc.Format("Stats.PerPercent", items), loc.Format("Stats.PerPercent.Tip", result.IntervalCount, cached));
            case PerPercentStatus.Total:
                return (loc.Format("Stats.PerPercent.Total", ShortenTokens(result.TotalPerPercent, loc)), "");
            default:
                return (loc["Stats.PerPercent.TooFew"], "");
        }
    }

    /// <summary>The estimate for one provider, kept until the records it was built from change.</summary>
    private PerPercentResult EstimatePerPercent(string providerId)
    {
        if (!ReferenceEquals(_perPercentSource, _sourceRecords))
        {
            _perPercent.Clear();
            _perPercentSource = _sourceRecords;
        }

        if (_perPercent.TryGetValue(providerId, out var cached))
            return cached;

        var now = DateTimeOffset.Now;
        var since = DateOnly.FromDateTime(DateTime.Now.AddDays(-StatsTokensPerPercent.LookbackDays));
        var records = _sourceRecords.Where(record => record.Provider == providerId && record.Day >= since).ToList();
        var weekly = LoadHistory(providerId, now.AddDays(-StatsTokensPerPercent.LookbackDays), now)
            .Where(point => point.Window == WindowKind.Weekly && StatsLimitsAggregator.LabelKeyOf(point) == "Window_Weekly")
            .ToList();
        var result = StatsTokensPerPercent.Estimate(StatsTokensPerPercent.BuildIntervals(records, weekly));
        _perPercent[providerId] = result;
        return result;
    }

    private static string FormatLimitPercent(double percent) =>
        StatusTextMap.FormatPercent(StatusTextMap.UsagePercent(percent).ToString(CultureInfo.CurrentCulture));

    /// <summary>The session grouping's views of the range: the largest <see cref="MaxSessionBars"/>
    /// sessions as bars, every session as a table row (and as the exported rows), the largest
    /// first. A bar is named by its project and start; its hover text and the table's first column
    /// carry the details.</summary>
    private void BuildSessionViews(DateOnly from, DateOnly to, LocalizationService loc)
    {
        var sessions = StatsAggregator.SessionsInRange(ApplyFilters(_allSessions), from, to);
        var grandTotal = sessions.Sum(session => session.TotalTokens);

        var tableRows = new List<StatsSessionRowViewModel>(sessions.Count);
        var barRows = new List<StatsProjectRow>();
        var exportRows = new List<StatsRowViewModel>(sessions.Count);
        foreach (var session in sessions)
        {
            var project = string.IsNullOrEmpty(session.Project)
                ? loc["Stats.NoProject"]
                : StatsAggregator.ShortenProjectLabel(StatsAggregator.ProjectKey(session.Project));
            var start = session.FirstUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var duration = DurationFormatter.Describe(session.Duration);
            var model = string.IsNullOrEmpty(session.MainModel) ? "" : ModelDisplayNames.Resolve(session.MainModel);
            var totalText = session.TotalTokens.ToString("N0", CultureInfo.CurrentCulture);
            var tooltip = string.Format(CultureInfo.CurrentCulture, loc["Stats.Session.Tooltip"], project, start, duration, totalText, model);

            tableRows.Add(new StatsSessionRowViewModel(project, start, duration, totalText, model, tooltip));
            exportRows.Add(new StatsRowViewModel($"{project} · {start}", session.TotalTokens, totalText, tooltip));
            if (barRows.Count < MaxSessionBars)
            {
                var label = $"{StatsAggregator.MiddleEllipsis(project, SessionBarProjectChars)} · {start}";
                var startDay = session.StartDay;
                barRows.Add(new StatsProjectRow(
                    label, label, session.TotalTokens, grandTotal > 0 ? session.TotalTokens * 100.0 / grandTotal : 0,
                    startDay, DateOnly.FromDateTime(session.LastUtc.ToLocalTime().DateTime),
                    Color: ChartPalette.ForProvider(session.Provider), TooltipText: tooltip));
            }
        }

        SessionRows = tableRows;
        SessionChartRows = barRows;
        Rows = exportRows;
    }

    /// <summary>How many characters of the project name a session bar's label keeps.</summary>
    private const int SessionBarProjectChars = 18;

    /// <summary>The current period's own calendar-day length that the figures bar's "per day"
    /// average divides by: the fixed length <see cref="RangeStart"/> used for every named range, but
    /// never longer than the span from the earliest record ever stored up to today - a 12 month
    /// range over three months of data averages over those three months, not over a full year. "All" has
    /// no fixed length and takes that span as is. With no records at all it falls back to one
    /// day.</summary>
    private static int PeriodDayCount(string range, DateOnly from, DateOnly to, IReadOnlyList<StatsRecord> all)
    {
        if (all.Count == 0)
            return 1;

        var sinceFirstRecord = Math.Max(1, to.DayNumber - all.Min(record => record.Day).DayNumber + 1);
        return range == "All" ? sinceFirstRecord : Math.Min(to.DayNumber - from.DayNumber + 1, sinceFirstRecord);
    }

    /// <summary>A language switch while the window is open: the choice labels are looked up again
    /// and every text this view model composes itself (figures, labels, the day heading) is rebuilt
    /// from the records already loaded - no new read.</summary>
    public void RefreshLanguage()
    {
        foreach (var choice in RangeChoices)
            choice.RefreshLabel();
        foreach (var choice in GroupingChoices)
            choice.RefreshLabel();
        foreach (var choice in ColorByChoices)
            choice.RefreshLabel();
        foreach (var choice in ProviderChoices)
            choice.RefreshLabel();
        OnPropertyChanged(nameof(ModelFilterText));
        OnPropertyChanged(nameof(ProjectFilterText));
        UpdateCustomChoiceLabel();
        OnPropertyChanged(nameof(ChartTooltip));
        RecomputeFrom(_sourceRecords);
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
            BusiestDayDateText = DateLabels.WeekdayDayMonthShort(busiestDay, CultureInfo.CurrentCulture);
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

    /// <summary>Every record from the period compared against - empty for "All", which has no
    /// defined length to mirror.</summary>
    private static List<StatsRecord> PreviousPeriod(IReadOnlyList<StatsRecord> all, StatsPeriod period) =>
        period.HasPrevious
            ? all.Where(record => record.Day >= period.PreviousFrom && record.Day <= period.PreviousTo).ToList()
            : [];

    /// <summary>The days a range covers and the days it is compared against.</summary>
    internal readonly record struct StatsPeriod(DateOnly From, DateOnly To, DateOnly PreviousFrom, DateOnly PreviousTo, bool HasPrevious);

    /// <summary>The days of <paramref name="range"/> as of <paramref name="today"/>. The rolling
    /// ranges end today and are compared with the period of the same length right before them. This
    /// month and last month are calendar months, compared with the calendar month before them. A
    /// custom range runs between its two days (never past today), compared with the same number of
    /// days right before it. "All" has no comparison.</summary>
    internal static StatsPeriod ResolvePeriod(string range, DateOnly today, DateOnly customFrom, DateOnly customTo)
    {
        switch (range)
        {
            case "ThisMonth":
            case "LastMonth":
            {
                var first = new DateOnly(today.Year, today.Month, 1);
                if (range == "LastMonth")
                    first = first.AddMonths(-1);
                var last = range == "LastMonth" ? first.AddMonths(1).AddDays(-1) : today;
                var previousFirst = first.AddMonths(-1);
                return new StatsPeriod(first, last, previousFirst, first.AddDays(-1), true);
            }
            case CustomRange:
            {
                var to = customTo > today ? today : customTo;
                var from = customFrom > to ? to : customFrom;
                return SameLengthBefore(from, to);
            }
            case "All":
                return new StatsPeriod(DateOnly.MinValue, today, default, default, false);
            default:
                return SameLengthBefore(RangeStart(range, today), today);
        }
    }

    private static StatsPeriod SameLengthBefore(DateOnly from, DateOnly to)
    {
        var previousTo = from.AddDays(-1);
        return new StatsPeriod(from, to, previousTo.AddDays(-(to.DayNumber - from.DayNumber)), previousTo, true);
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
        "ThisMonth" => new DateOnly(today.Year, today.Month, 1),
        "LastMonth" => new DateOnly(today.Year, today.Month, 1).AddMonths(-1),
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
