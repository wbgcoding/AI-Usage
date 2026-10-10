namespace AiUsage.Stats;

/// <summary>One window of the limits section, already formatted for display: the window's name, how
/// often its limit was reached, the highest value and the average peak as text, and the two
/// percentages the bar draws.</summary>
public sealed record StatsLimitRowViewModel(
    string Label, string ReachedText, string HighestText, string AveragePeakText, string DetailText,
    double HighestPercent, double AveragePeakPercent, int ReachedCount)
{
    /// <summary>The bar's color step: red once the limit was reached, amber from 80 percent.</summary>
    public string Level => ReachedCount > 0 ? "Crit" : HighestPercent >= 80 ? "Warn" : "Ok";
}

/// <summary>One provider's windows in the limits section, under the provider's name, and the line
/// shown under them: the tokens one percent of the week costs, or why there is no such figure. The
/// line is empty for a provider the estimate does not cover.</summary>
public sealed record StatsLimitGroupViewModel(
    string ProviderId, string ProviderName, IReadOnlyList<StatsLimitRowViewModel> Rows, string NoteText, string NoteToolTip)
{
    public bool HasNote => NoteText.Length > 0;
}
