namespace AiUsage.Stats;

/// <summary>One window of the limits section, already formatted for display: the window's name, how
/// often its limit was reached out of how many cycles, the highest value and the average peak as text, the
/// two percentages the bar draws and, once the limit was reached, when last and for how long. A limit reset used in
/// the range adds its own line (<see cref="ResetUsedText"/>) with an explaining tooltip.</summary>
public sealed record StatsLimitRowViewModel(
    string Label, string ReachedText, string HighestText, string AveragePeakText, string DetailText,
    double HighestPercent, double AveragePeakPercent, int ReachedCount, int PeriodCount, string LastReachedText = "",
    string WindowKey = "", string ResetUsedText = "", string ResetUsedTip = "")
{
    /// <summary>Share of cycles at the limit from which the bar turns red.</summary>
    private const double CritShare = 0.25;

    /// <summary>The bar's color step: red when a quarter of the cycles or more hit the limit, amber after any hit
    /// or from 80 percent, else ok.</summary>
    public string Level =>
        PeriodCount > 0 && ReachedCount / (double)PeriodCount >= CritShare ? "Crit"
        : ReachedCount > 0 || HighestPercent >= 80 ? "Warn"
        : "Ok";

    /// <summary>True when the row has a "last at the limit" line.</summary>
    public bool HasLastReached => LastReachedText.Length > 0;

    /// <summary>True when a limit reset was used in the range.</summary>
    public bool HasResetUsed => ResetUsedText.Length > 0;

    /// <summary>What a screen reader announces for the row: the window's name, then how often it was
    /// reached, the detail and when last, joined in the label-value order and punctuation of the current language.</summary>
    public string AutomationText => Services.TextCompose.List([ReachedText, DetailText, LastReachedText, ResetUsedText]) is { Length: > 0 } value
        ? Services.LocalizationService.Instance.Format("Format.LabelValue", Label, value)
        : Label;
}

/// <summary>One provider's windows in the limits section, under the provider's name. <see cref="PeriodText"/>
/// names the span the readings cover.</summary>
public sealed record StatsLimitGroupViewModel(
    string ProviderId, string ProviderName, IReadOnlyList<StatsLimitRowViewModel> Rows, string PeriodText = "")
{
    public bool HasPeriod => PeriodText.Length > 0;
}
