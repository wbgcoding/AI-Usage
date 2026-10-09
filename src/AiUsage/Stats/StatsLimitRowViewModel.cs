namespace AiUsage.Stats;

/// <summary>One row of the limits section, already formatted for display: the provider and window it
/// names, how often the limit was reached, the highest value and the average peak.</summary>
public sealed record StatsLimitRowViewModel(string Label, string ReachedText, string HighestText, string AveragePeakText);

/// <summary>One provider's rows in the limits section and the line shown under them: the tokens one
/// percent of the week costs, or why there is no such figure. The line is empty for a provider the
/// estimate does not cover.</summary>
public sealed record StatsLimitGroupViewModel(string ProviderId, IReadOnlyList<StatsLimitRowViewModel> Rows, string NoteText, string NoteToolTip)
{
    public bool HasNote => NoteText.Length > 0;
}
