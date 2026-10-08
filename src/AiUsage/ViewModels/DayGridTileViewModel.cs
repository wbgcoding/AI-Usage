using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

/// <summary>
/// The widget's own "Verbrauch je Tag"/"Usage per day" tile - a trailing-weeks day-grid, like the statistics
/// window's own <see cref="Views.Controls.StatsMonthGrid"/> section, drawn with that very same
/// control and fed from the very same <see cref="Stats.StatsStore"/> through <see
/// cref="StatsMonthGridBuilder"/>, so the two never disagree about a day's color or total. Takes
/// part in the widget's own tile ordering and hiding exactly like a <see
/// cref="ProviderTileViewModel"/> (see <see cref="ProviderId"/> and <see cref="ITileRow"/>), but is
/// never a real provider: it has no snapshot, no fetch, no sign-in.
/// </summary>
public sealed partial class DayGridTileViewModel : ObservableObject, ITileRow
{
    private readonly StatsStore? _statsStore;

    private readonly Func<DateOnly> _clock;

    /// <summary>The by-day list from the oldest week the tile has ever needed (or the twelve-month
    /// start, whichever is earlier) through today - always what <see cref="Days"/> hands the embedded
    /// <see cref="Views.Controls.StatsMonthGrid"/>. Widening the tile only moves <see
    /// cref="RangeStart"/> (see <see cref="RecomputeVisibleRange"/>); the list is rebuilt from
    /// <see cref="_records"/> only when that start goes older than the list's first day.</summary>
    private IReadOnlyList<StatsMonthGrid.DayValue> _allDays = [];

    /// <summary>The store's records as of the last refresh, kept so a resize re-slices in memory
    /// instead of reading the store again; null until a refresh has found a store.</summary>
    private IReadOnlyList<StatsRecord>? _records;
    private int _refreshSequence;

    /// <param name="clock">The current local date; a test hands in a fixed one so the early-January
    /// case (the range reaching into the previous year) is reproducible.</param>
    public DayGridTileViewModel(StatsStore? statsStore, Func<DateOnly>? clock = null)
    {
        _statsStore = statsStore;
        _clock = clock ?? (() => DateOnly.FromDateTime(DateTime.Now));
        Today = _clock();
        RangeStart = Today;
        RangeEnd = Today;
    }

    /// <summary>This pseudo tile's own identity in the widget's shared order/hidden settings (see
    /// <see cref="Models.AppSettings.DayGridTileId"/>) - never a real account key.</summary>
    public string ProviderId => AppSettings.DayGridTileId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleVisibilityActionText))]
    private bool isHidden;

    [ObservableProperty]
    private bool canMoveUp = true;

    [ObservableProperty]
    private bool canMoveDown = true;

    /// <summary>Full/Mini, assigned by MainWindow exactly like a provider tile's own <see
    /// cref="ProviderTileViewModel.Density"/> - Mini is this spec's "Small": a single narrow strip of
    /// the last seven days, no weekday/month labels, no legend (see <see cref="ShowAxisLabels"/>/
    /// <see cref="ShowLegend"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLegend))]
    [NotifyPropertyChangedFor(nameof(ShowAxisLabels))]
    [NotifyPropertyChangedFor(nameof(ShowAsStrip))]
    private TileDensity density = TileDensity.Full;

    partial void OnDensityChanged(TileDensity value) => RecomputeVisibleRange();

    /// <summary>The tile's own rendered width, fed by the view's <c>SizeChanged</c> (a plain
    /// ViewModel property has no other way to learn it) - <see cref="RecomputeVisibleRange"/> reads
    /// this to decide how many of the most recent weeks fit at the current density's cell size,
    /// the same "shrink the content, never clip it" contract every other tile follows.</summary>
    [ObservableProperty]
    private double availableWidth = 300;

    partial void OnAvailableWidthChanged(double value) => RecomputeVisibleRange();

    /// <summary>False once <see cref="Refresh"/> has run against a usable store with at least one
    /// record ever indexed - not just within the shown span, since a day-grid trimmed to the last few
    /// weeks must not claim "no usage" for a year that has plenty, just not recently.</summary>
    [ObservableProperty]
    private bool hasAnyData;

    /// <summary>The whole loaded span, unsliced - see <see cref="_allDays"/>. Bound straight to
    /// <see cref="Views.Controls.StatsMonthGrid.Days"/>.</summary>
    public IReadOnlyList<StatsMonthGrid.DayValue> Days { get; private set; } = [];

    public DateOnly RangeStart { get; private set; }

    public DateOnly RangeEnd { get; private set; }

    public DateOnly Today { get; private set; }

    /// <summary>Quantile boundaries of the color scale over the whole recorded history, so the shade
    /// of a day does not change when a resize moves <see cref="RangeStart"/>. Null before data.</summary>
    public IReadOnlyList<long>? ColorScaleTotals { get; private set; }

    /// <summary>Full only: Mini drops the color-key/intensity-scale row for lack of room.</summary>
    public bool ShowLegend => Density == TileDensity.Full;

    /// <summary>Everything but Mini: Mini's own single narrow strip has no room for the weekday
    /// column or the month row either.</summary>
    public bool ShowAxisLabels => Density != TileDensity.Mini;

    /// <summary>Mini only: the last seven days as one row, not as the week-column that would split
    /// them over two partial columns.</summary>
    public bool ShowAsStrip => Density == TileDensity.Mini;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Bound from XAML against this instance; the widget's own eye-menu row template binds it on any row it renders.")]
    public string HeaderDisplayName => LocalizationService.Instance["Stats.MonthGrid.Head"];

    /// <summary>Same wording and shape as <see cref="ProviderTileViewModel.ToggleVisibilityActionText"/> -
    /// the eye popup's row template binds this property by name on whichever kind of row it is
    /// showing, so the two must agree on it.</summary>
    public string ToggleVisibilityActionText =>
        $"{HeaderDisplayName}: {LocalizationService.Instance[IsHidden ? "Tile.Show" : "Tile.Hide"]}";

    /// <summary>The header row's own right-hand figure: the total of whatever span is actually shown
    /// (the visible weeks at Full, the last seven days at Mini) - not the whole year, so it
    /// always agrees with what the grid underneath it is drawing.</summary>
    public string TotalText => StatsAggregator.ShortenTokenCountCompact(
        _allDays.Where(day => day.Day >= RangeStart && day.Day <= RangeEnd).Sum(day => day.Total),
        LocalizationService.Instance["Stats.RingCompact.Thousand"],
        LocalizationService.Instance["Stats.RingCompact.Million"],
        LocalizationService.Instance["Stats.RingCompact.Billion"]);

    /// <summary>Raised on a click on a real, non-future day - MainWindow opens (or focuses) the
    /// statistics window and selects this exact day there, reusing its own existing day-selection
    /// (see <see cref="Views.StatsWindow.SelectDay"/>) rather than building a second one.</summary>
    public event EventHandler<DateOnly>? DaySelected;

    [RelayCommand]
    private void SelectDay(DateOnly day)
    {
        if (day > Today)
            return;
        DaySelected?.Invoke(this, day);
    }

    /// <summary>The tile's own context-menu "Ausblenden"/"Hide" entry - same split as <see
    /// cref="ProviderTileViewModel.HideRequested"/>: MainViewModel owns the actual persistence, this
    /// tile only ever announces the request.</summary>
    public event EventHandler? HideRequested;

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Reloads from <see cref="_statsStore"/> - called once at startup and again every time
    /// <see cref="Services.StatsIndexerService.IndexCompleted"/> fires, so the tile never needs the
    /// statistics window to have been opened even once to show real numbers.</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>The awaitable form of <see cref="Refresh"/>: the index is read and the grid built on a
    /// pool thread, the result is assigned back on the caller's context. A newer call makes an older
    /// one still running drop its result, so a slow early load never overwrites a fresher one.</summary>
    public async Task RefreshAsync()
    {
        var sequence = Interlocked.Increment(ref _refreshSequence);
        var now = _clock();
        if (now != Today)
        {
            Today = now;
            // The grid binds Today: without this it keeps the startup date after midnight.
            OnPropertyChanged(nameof(Today));
        }

        IReadOnlyList<StatsMonthGrid.DayValue> days;
        IReadOnlyList<StatsRecord>? records = null;
        IReadOnlyList<long>? colorScale = null;
        bool hasAnyData;
        if (_statsStore is not { } store)
        {
            days = [];
            hasAnyData = false;
        }
        else
        {
            var today = Today;
            var wantedStart = WantedRangeStart(today);
            try
            {
                (days, hasAnyData, records, colorScale) = await Task.Run(() => LoadDays(store, today, wantedStart));
            }
            catch (Exception ex)
            {
                LogService.Shared.LogError($"Day grid load failed ({ex.GetType().Name}): {ex.Message}");
                return;
            }

            if (sequence != Volatile.Read(ref _refreshSequence))
                return;
        }

        _allDays = days;
        _records = records;
        ColorScaleTotals = colorScale;
        OnPropertyChanged(nameof(ColorScaleTotals));
        HasAnyData = hasAnyData;
        Days = _allDays;
        OnPropertyChanged(nameof(Days));
        RecomputeVisibleRange();
    }

    private static (IReadOnlyList<StatsMonthGrid.DayValue> Days, bool HasAnyData, IReadOnlyList<StatsRecord> Records, IReadOnlyList<long> ColorScale)
        LoadDays(StatsStore store, DateOnly today, DateOnly wantedStart)
    {
        var all = store.LoadAll();
        // From the oldest week the tile shows at its current width: those columns reach back past
        // January 1st into the previous year (the seven-day strip needs that in early January too),
        // and before the first recorded day as empty cells.
        var built = StatsMonthGridBuilder.Build(all, today, from: wantedStart);
        return (built.Days, all.Count > 0, all, built.ColorScaleTotals);
    }

    /// <summary>Refreshes every piece of text this tile resolves from <see
    /// cref="LocalizationService"/> directly rather than through a binding path WPF re-evaluates on
    /// its own - the same follow-up <see cref="MainViewModel"/> already runs for every provider tile
    /// on a language switch.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(HeaderDisplayName));
        OnPropertyChanged(nameof(ToggleVisibilityActionText));
        OnPropertyChanged(nameof(TotalText));
    }

    /// <summary>How many of the most recent weeks fit <paramref name="availableWidth"/> at the grid's
    /// normal cell size - Mini always answers 1 (its single strip is always exactly the last seven
    /// days, whatever the width). Not capped (the tile reaches back as far as its width allows) and
    /// never less than 1 (at least the current week always shows, however narrow the tile).</summary>
    internal static int ComputeVisibleWeeks(TileDensity density, double availableWidth)
    {
        if (density == TileDensity.Mini)
            return 1;

        return StatsMonthGrid.ColumnsFitting(availableWidth);
    }

    /// <summary>The first day the current density and width show for <paramref name="today"/>.</summary>
    private DateOnly WantedRangeStart(DateOnly today)
    {
        if (Density == TileDensity.Mini)
            return today.AddDays(-6);

        var weeks = ComputeVisibleWeeks(Density, AvailableWidth);
        var weekStart = StatsAggregator.WeekStart(today);
        // Never before the first representable week: a bogus width must not underflow the date.
        weeks = Math.Min(weeks, Math.Max(1, (weekStart.DayNumber - DateOnly.MinValue.DayNumber) / 7));
        return weekStart.AddDays(-(weeks - 1) * 7);
    }

    /// <summary>Recomputes <see cref="RangeStart"/>/<see cref="RangeEnd"/> from the current <see
    /// cref="Density"/> and <see cref="AvailableWidth"/>: the trailing weeks that fit, always ending
    /// today (no future weeks), so a wider tile adds one older week at the left. <see cref="Days"/>
    /// is rebuilt in memory (no store read) only when the start goes older than its first day.</summary>
    private void RecomputeVisibleRange()
    {
        RangeEnd = Today;
        RangeStart = WantedRangeStart(Today);

        if (_records is { } records && (_allDays.Count == 0 || RangeStart < _allDays[0].Day))
        {
            _allDays = StatsMonthGridBuilder.Build(records, Today, from: RangeStart, colorScale: ColorScaleTotals).Days;
            Days = _allDays;
            OnPropertyChanged(nameof(Days));
        }

        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(TotalText));
    }
}
