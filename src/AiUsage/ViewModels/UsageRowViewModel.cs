using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One percentage bar on a tile - the 5-hour window, the weekly
/// window, or an "Other" one a provider adds beyond those two.</summary>
public partial class UsageRowViewModel : ObservableObject
{
    private DateTimeOffset? _resetsAt;

    /// <summary>Length of this window in minutes, null when the provider does not say - the other half
    /// (next to <see cref="_resetsAt"/>) of what <see cref="PacePercent"/> is computed from.</summary>
    private int? _windowMinutes;

    /// <summary>The tile's own density, mirrored here only because <see cref="ClockText"/> depends on
    /// it (Full only) - this row never decides its own density, it is always handed in by <see
    /// cref="ProviderTileViewModel"/> through the constructor or <see cref="Update"/>.</summary>
    private TileDensity _density;

    public string LabelText { get; private set; }

    /// <summary>The row's raw identity (<see cref="UsageWindow.Label"/>, a resource key or a literal
    /// provider-supplied name), next to the already-localized <see cref="LabelText"/> - <see
    /// cref="Views.MainWindow.TryComputeTraySummary"/> matches a "Label:&lt;label&gt;" tray window
    /// choice against this, since two Other rows share the same <see cref="Kind"/> and can only be
    /// told apart by their label. Never reassigned after construction, same as <see cref="Kind"/>.</summary>
    public string Label { get; private set; }

    /// <summary>Raw window kind, next to the already-localized <see cref="LabelText"/> - the tray
    /// tooltip needs "5h"/"7d" as fixed short tokens, not whatever the active
    /// language's full label happens to say. Never reassigned after construction: it is this row's
    /// identity, matched against the incoming window order before an in-place <see cref="Update"/>
    /// is even attempted.</summary>
    public WindowKind Kind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightLabelText))]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    [NotifyPropertyChangedFor(nameof(AllowanceText))]
    [NotifyPropertyChangedFor(nameof(PaceText))]
    [NotifyPropertyChangedFor(nameof(BarTooltipText))]
    [NotifyPropertyChangedFor(nameof(MiniPercentText))]
    [NotifyPropertyChangedFor(nameof(MiniAccessibleName))]
    private double usedPercent;

    /// <summary>"63 %" - the Mini tile row's own percentage text, rounded through the same <see
    /// cref="StatusTextMap.UsagePercent"/> as <see cref="RightLabelText"/> and the notification
    /// balloon, so a mid-point value never reads one number on the tile and another in the popup.</summary>
    public string MiniPercentText => RoundedPercentText();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightLabelText))]
    [NotifyPropertyChangedFor(nameof(LevelName))]
    [NotifyPropertyChangedFor(nameof(ShowAlertIcon))]
    [NotifyPropertyChangedFor(nameof(BarAccessibleName))]
    [NotifyPropertyChangedFor(nameof(MiniAccessibleName))]
    private UsageLevel level;

    /// <summary>The level in words for a screen reader ("nearly used up", "almost at the limit"),
    /// empty while the window is fine - colour and icon alone must not carry the warning.</summary>
    public string LevelName => Level switch
    {
        UsageLevel.Warn => Loc["Level.WarnName"],
        UsageLevel.Crit => Loc["Level.CritName"],
        _ => "",
    };

    /// <summary>Whether the alert glyph shows: the Warn level only, since Crit carries its own warning
    /// triangle.</summary>
    public bool ShowAlertIcon => Level == UsageLevel.Warn;

    /// <summary>The bar's automation name: the window label, plus the level name once it is not Ok.</summary>
    public string BarAccessibleName => LevelName.Length > 0 ? $"{LabelText}, {LevelName}" : LabelText;

    /// <summary>The Mini row's automation name: label and percentage, plus the level name once it is not Ok.</summary>
    public string MiniAccessibleName => LevelName.Length > 0
        ? $"{LabelText}, {SpokenPercentText}, {LevelName}"
        : $"{LabelText}, {SpokenPercentText}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightLabelText))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string countdownText = "";

    /// <summary>The reset instant's local wall-clock time ("21:15"/"Mi 21:15"), on top of the bare
    /// relative <see cref="CountdownText"/> - only ever non-empty at <see cref="TileDensity.Full"/>
    /// (the Mini tooltip and the tray balloon both want the short relative text alone, not this).
    /// Recomputed everywhere <see cref="CountdownText"/> is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightLabelText))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string clockText = "";

    /// <summary>"full in about 2h 5m" - empty whenever <see cref="UsageForecast.TimeToFull"/> has no
    /// confident answer, or the caller decided a forecast does not belong on this row at all (not
    /// Full density, or the provider is stale/silent/failed). Set only through <see
    /// cref="UpdateForecast"/>/<see cref="ClearForecast"/>, never computed here directly - this row
    /// has no access to the chart history or the tile's own density/status.</summary>
    [ObservableProperty]
    private string forecastText = "";

    /// <summary>Real token count read alongside this window, or null when the
    /// provider has no local source for it - never a placeholder, the row simply omits the segment.</summary>
    public long? TokenCount { get; private set; }

    /// <summary>This provider's token sum for the current week, handed in by the tile - shown on a
    /// weekly row that has no token count of its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InlineTokensText))]
    [NotifyPropertyChangedFor(nameof(InlineTokensToolTip))]
    private long? weekTokens;

    /// <summary>This window's notification threshold, in percent - null when that window's
    /// notification is switched off in Settings, in which case the bar shows no marker at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThresholdMarker))]
    [NotifyPropertyChangedFor(nameof(ThresholdMarkerTooltip))]
    private double? thresholdPercent;

    /// <summary>The marker only ever shows at Full density: Mini has no bar left to put a
    /// marker on.</summary>
    public bool ShowThresholdMarker => ThresholdPercent is not null && _density == TileDensity.Full;

    public string ThresholdMarkerTooltip => ThresholdPercent is { } percent
        ? Loc.Format("Tile.ThresholdMarker", StatusTextMap.UsagePercent(percent))
        : "";

    /// <summary>Assigns <see cref="_density"/> and, only when it actually changes, raises the
    /// notification for the two properties that depend on it (<see cref="ShowThresholdMarker"/>,
    /// <see cref="ThresholdMarkerTooltip"/>) - a plain field write raises nothing on its own, which
    /// left the marker stuck at its old visibility across a density switch until the row was rebuilt.</summary>
    private void SetDensity(TileDensity density)
    {
        if (density == _density)
            return;
        _density = density;
        OnPropertyChanged(nameof(ShowThresholdMarker));
        OnPropertyChanged(nameof(ThresholdMarkerTooltip));
    }

    /// <summary>Mirrors <see cref="UsageWindow.Allowance"/> for <see cref="AllowanceText"/> - null
    /// when the provider's response carries no used/total denominator.</summary>
    private UsageAllowance? _allowance;

    /// <summary>"42 % of 500 requests used (210 so far)" - the bar's own tooltip once the provider's
    /// response carries a real denominator; empty (no tooltip at all) when it does not, never a
    /// placeholder dash.</summary>
    public string AllowanceText => _allowance is { } allowance
        ? Loc.Format(
            "Window.Allowance",
            StatusTextMap.UsagePercent(UsedPercent),
            allowance.Used.ToString("N0", CultureInfo.CurrentCulture),
            allowance.Total.ToString("N0", CultureInfo.CurrentCulture),
            Loc[allowance.UnitKey])
        : "";

    /// <summary>How far through its window the clock is, in percent (0-100 exclusive), or null when the
    /// window length or the reset instant is unknown or the result falls outside the window (a reset
    /// that already passed). The bar draws it as a tick; usage beyond it means the window is being
    /// used faster than time passes.</summary>
    public double? PacePercent { get; private set; }

    /// <summary>"60 % of the time has passed, usage is ahead of it." - how the used percentage compares
    /// with <see cref="PacePercent"/>; empty when there is no pace.</summary>
    public string PaceText
    {
        get
        {
            if (PacePercent is not { } pace)
                return "";
            var difference = UsedPercent - pace;
            var key = difference > PaceTolerance ? "Tile.Pace.Above" : difference < -PaceTolerance ? "Tile.Pace.Below" : "Tile.Pace.On";
            return Loc.Format(key, StatusTextMap.UsagePercent(pace).ToString(CultureInfo.CurrentCulture));
        }
    }

    /// <summary>Percentage points the usage may sit away from the pace before it counts as ahead or below.</summary>
    private const double PaceTolerance = 3;

    /// <summary>The bar's tooltip: the allowance line, then the pace line; empty (no tooltip) only when
    /// both are.</summary>
    public string BarTooltipText => AllowanceText.Length > 0 && PaceText.Length > 0
        ? AllowanceText + Environment.NewLine + PaceText
        : AllowanceText.Length > 0 ? AllowanceText : PaceText;

    private double? ComputePacePercent(DateTimeOffset now)
    {
        if (_resetsAt is not { } reset || _windowMinutes is null or <= 0)
            return null;
        var pace = 100 * (1 - (reset - now).TotalMinutes / _windowMinutes.Value);
        return pace is > 0 and < 100 ? pace : null;
    }

    /// <summary>Recomputes <see cref="PacePercent"/> and raises the dependent properties only when the
    /// marker would visibly move (a tenth of a percent), so the once-a-second tick does not churn bindings.</summary>
    private void UpdatePace(DateTimeOffset now)
    {
        var pace = ComputePacePercent(now);
        var sameState = pace is null ? PacePercent is null : PacePercent is { } old && Math.Abs(pace.Value - old) < 0.1;
        if (sameState)
            return;
        PacePercent = pace;
        OnPropertyChanged(nameof(PacePercent));
        OnPropertyChanged(nameof(PaceText));
        OnPropertyChanged(nameof(BarTooltipText));
    }

    /// <summary>"42 %" - the bare rounded percentage in the active language's percent convention.
    /// The first part of <see cref="RightLabelText"/>.</summary>
    public string PercentText => RoundedPercentText();

    /// <summary>"42 % genutzt" - the percentage named as what it is, for the screen reader name only;
    /// the visible text stays the bare number.</summary>
    private string SpokenPercentText => Loc.Format("Window.UsedPercent", StatusTextMap.UsagePercent(UsedPercent).ToString(CultureInfo.CurrentCulture));

    /// <summary>"42 % · noch 2h 14m" - the percent text itself carries no glyph; colour, plus the
    /// separate warning icon `UsageBar.xaml` shows next to it, carry the Crit warning instead. Token
    /// count and forecast sit beside the window label instead (<see cref="InlineTokensText"/>,
    /// <see cref="ForecastText"/>), so this part never loses the percent or the countdown to make
    /// room for either.</summary>
    public string RightLabelText => DetailText.Length > 0 ? $"{PercentText} · {DetailText}" : PercentText;

    /// <summary>"noch 2h 14m · 21:15" - the countdown and the reset clock time, without the
    /// percentage, so the header can set them smaller and greyer than the number. Empty when neither
    /// is known.</summary>
    public string DetailText
    {
        get
        {
            var segments = new List<string>();
            if (CountdownText.Length > 0)
                segments.Add(CountdownText);
            if (ClockText.Length > 0)
                segments.Add(ClockText);
            return string.Join(" · ", segments);
        }
    }

    /// <summary>"1,8 Mio Token" - the window's own token count, or on a weekly row the tile's week
    /// sum, shortened and in the OS regional number format. Empty when neither exists, which hides
    /// the segment; the exact figure is <see cref="InlineTokensToolTip"/>.</summary>
    public string InlineTokensText => InlineTokens is { } tokens
        ? Loc.Format(
            "Window.TokensInline",
            StatsAggregator.ShortenTokenCount(tokens, Loc["Number.Thousand"], Loc["Number.Million"], Loc["Number.Billion"]))
        : "";

    /// <summary>The exact count behind <see cref="InlineTokensText"/> with what it covers ("in this
    /// window" or "this week"); empty when the text is.</summary>
    public string InlineTokensToolTip
    {
        get
        {
            if (InlineTokens is not { } tokens)
                return "";
            return Loc.Format(TokenCount is null ? "Tile.TokensThisWeek" : "Window.TokensThisSession", tokens.ToString("N0", CultureInfo.CurrentCulture));
        }
    }

    private long? InlineTokens => TokenCount ?? (Kind == WindowKind.Weekly ? WeekTokens : null);

    private string RoundedPercentText() =>
        StatusTextMap.FormatPercent(StatusTextMap.UsagePercent(UsedPercent).ToString(CultureInfo.CurrentCulture));

    public UsageRowViewModel(
        UsageWindow window, DateTimeOffset now, TileDensity density = TileDensity.Full, double? thresholdPercent = null)
    {
        LabelText = StatusTextMap.Resolve(window.Label);
        Label = window.Label;
        Kind = window.Kind;
        _resetsAt = window.ResetsAt;
        _windowMinutes = window.WindowMinutes;
        _density = density;
        _allowance = window.Allowance;
        PacePercent = ComputePacePercent(now);
        UsedPercent = window.UsedPercent;
        Level = Classify(window.UsedPercent);
        CountdownText = ComputeCountdownText(now);
        ClockText = ComputeClockText(now);
        TokenCount = window.Tokens?.TotalTokens;
        ThresholdPercent = thresholdPercent;
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>Matches the display rounding that shows "100 %" - the row reports itself as at its
    /// limit exactly when the number on screen would already read 100, never at the merely-Crit 95%
    /// that used to look identical to it.</summary>
    private const double LimitReachedPercent = 99.5;

    private bool IsAtLimit => UsedPercent >= LimitReachedPercent;

    /// <summary>The countdown text is the one place a row is at its limit becomes visible in the
    /// running UI text: once there, it names that state and, when a reset instant is actually known,
    /// also says when the window frees again - a bare "limit reached" with no known reset time would
    /// otherwise look identical to one that will never lift.</summary>
    private string ComputeCountdownText(DateTimeOffset now)
    {
        if (!IsAtLimit)
            return CountdownFormatter.Format(_resetsAt, now);

        var limitText = Loc["State.LimitReached"];
        if (_resetsAt is not { } reset)
            return limitText;

        // The bare duration, not "free again in 45m": at the limit the row already carries the
        // percentage, the state and the wall-clock time of the reset, and the whole line has to fit
        // without anything being cut off - the sentence around the number is what goes.
        return $"{limitText} · {CountdownFormatter.FormatElapsed(reset - now)}";
    }

    /// <summary>Non-empty at every density once a reset instant is known - whether it actually fits
    /// next to the percent value is a layout question now, answered by the tile's own width
    /// (<see cref="Views.Controls.TileWidthToVisibilityConverter"/> on the Mini row), not by the
    /// density this row was handed. Shows through the limit too: the clock is the one segment that
    /// answers "when exactly" regardless of whether the row also names the limit.</summary>
    private string ComputeClockText(DateTimeOffset now) => CountdownFormatter.FormatClock(_resetsAt, now);

    /// <summary>Called once a second by <see cref="ProviderTileViewModel.TickCountdowns"/>, and once
    /// more whenever the tile's own density changes (a density switch is the only other event that
    /// can flip <see cref="ClockText"/> between empty and not, with no new snapshot involved). Both
    /// texts only actually change once a minute (they carry no seconds), so most calls would
    /// otherwise reassign the same string and rebuild <see cref="RightLabelText"/> for nothing -
    /// computed first and assigned only when they differ from what the row already holds.</summary>
    public void RefreshCountdown(DateTimeOffset now, TileDensity density)
    {
        SetDensity(density);
        UpdatePace(now);
        var countdownText = ComputeCountdownText(now);
        if (countdownText != CountdownText)
            CountdownText = countdownText;
        var clockText = ComputeClockText(now);
        if (clockText != ClockText)
            ClockText = clockText;
    }

    /// <summary>Recomputes <see cref="ForecastText"/> from this row's own chart history, using the
    /// reset instant already held in <see cref="_resetsAt"/> - the caller (<see
    /// cref="ProviderTileViewModel.UpdateHistory"/>) decides whether a forecast belongs on this row
    /// at all (density, staleness, silence) and simply does not call this when it does not.</summary>
    internal void UpdateForecast(IReadOnlyList<HistoryChart.ChartPoint> points, DateTimeOffset now, TimeSpan? lookback)
    {
        // A window at its limit has nothing left to forecast; the bar already says the limit is reached.
        var forecast = IsAtLimit ? null : UsageForecast.TimeToFull(points, now, _resetsAt, lookback: lookback);
        ForecastText = forecast is { } duration
            ? Loc.Format("Window.ForecastFull", CountdownFormatter.FormatElapsed(duration))
            : "";
    }

    internal void ClearForecast() => ForecastText = "";

    /// <summary>Reassigns every field the constructor sets, without replacing the instance - keeps
    /// this same row (and any live focus/tooltip inside its bound `UsageBar` visual) across a refresh
    /// whose window kinds did not change. Caller (<see cref="ProviderTileViewModel.Apply"/>) only
    /// calls this once it has already confirmed <paramref name="window"/>.Kind matches <see cref="Kind"/>.</summary>
    internal void Update(UsageWindow window, DateTimeOffset now, TileDensity density, double? thresholdPercent)
    {
        // Rows are matched by kind alone, so two "other" rows (one per model) can swap their labels
        // between refreshes - the raw label must follow, or the tray's label choice reads the wrong row.
        Label = window.Label;
        LabelText = StatusTextMap.Resolve(window.Label);
        OnPropertyChanged(nameof(LabelText));
        OnPropertyChanged(nameof(LevelName));
        OnPropertyChanged(nameof(BarAccessibleName));
        OnPropertyChanged(nameof(MiniAccessibleName));
        _resetsAt = window.ResetsAt;
        _windowMinutes = window.WindowMinutes;
        SetDensity(density);
        _allowance = window.Allowance;
        PacePercent = ComputePacePercent(now);
        UsedPercent = window.UsedPercent;
        Level = Classify(window.UsedPercent);
        CountdownText = ComputeCountdownText(now);
        ClockText = ComputeClockText(now);
        TokenCount = window.Tokens?.TotalTokens;
        ThresholdPercent = thresholdPercent;
        OnPropertyChanged(nameof(TokenCount));
        // A language switch re-applies the same snapshot: the percent is unchanged, so its own setter
        // raises nothing, yet the texts built around it follow the language.
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(MiniPercentText));
        OnPropertyChanged(nameof(ThresholdMarkerTooltip));
        OnPropertyChanged(nameof(RightLabelText));
        OnPropertyChanged(nameof(InlineTokensText));
        OnPropertyChanged(nameof(InlineTokensToolTip));
        OnPropertyChanged(nameof(AllowanceText));
        OnPropertyChanged(nameof(PacePercent));
        OnPropertyChanged(nameof(PaceText));
        OnPropertyChanged(nameof(BarTooltipText));
    }

    /// <summary>The colour boundaries, also shown to the user next to the notification threshold
    /// (a separately configurable value) so "yellow"/"red" never means a hidden number nobody can
    /// see - <c>SettingsViewModel.LevelExplainerText</c> renders both into a sentence.</summary>
    public const double WarnFrom = 60;

    public const double CritFrom = 85;

    /// <summary>0-60% Ok, &gt;60-85% Warn, &gt;85-100% Crit.</summary>
    public static UsageLevel Classify(double usedPercent) => usedPercent switch
    {
        <= WarnFrom => UsageLevel.Ok,
        <= CritFrom => UsageLevel.Warn,
        _ => UsageLevel.Crit,
    };
}
