namespace AiUsage.Stats;

/// <summary>One window of the limits section, already formatted for display: the window's name, how
/// often its limit was reached, the highest value and the average peak as text, and the two
/// percentages the bar draws.</summary>
public sealed record StatsLimitRowViewModel(
    string Label, string ReachedText, string HighestText, string AveragePeakText, string DetailText,
    double HighestPercent, double AveragePeakPercent, int ReachedCount, string WindowKey = "")
{
    /// <summary>The bar's color step: red once the limit was reached, amber from 80 percent.</summary>
    public string Level => ReachedCount > 0 ? "Crit" : HighestPercent >= 80 ? "Warn" : "Ok";

    /// <summary>What a screen reader announces for the row: the window's name, then how often it was
    /// reached and the detail, joined in the label-value order and punctuation of the current language.</summary>
    public string AutomationText => Services.TextCompose.List([ReachedText, DetailText]) is { Length: > 0 } value
        ? Services.LocalizationService.Instance.Format("Format.LabelValue", Label, value)
        : Label;
}

/// <summary>One provider's windows in the limits section, under the provider's name.</summary>
public sealed record StatsLimitGroupViewModel(
    string ProviderId, string ProviderName, IReadOnlyList<StatsLimitRowViewModel> Rows);
