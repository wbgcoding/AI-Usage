namespace AiUsage.Stats;

/// <summary>One row of the limits section, already formatted for display: the provider and window it
/// names, how often the limit was reached, the highest value and the average peak.</summary>
public sealed record StatsLimitRowViewModel(string Label, string ReachedText, string HighestText, string AveragePeakText);
