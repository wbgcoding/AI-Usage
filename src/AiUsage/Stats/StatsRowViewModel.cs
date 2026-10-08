namespace AiUsage.Stats;

/// <summary>One row of the statistics table - already formatted for display, so the view stays a
/// plain binding with no value conversion of its own. <see cref="ToolTip"/> is empty for every row
/// whose <see cref="Label"/> is already the whole story (a day, a week, a provider, a model); a
/// project row that had its own full path shortened down to <see cref="Label"/> carries that full
/// path back here instead, so a hover can still name it exactly.</summary>
public sealed record StatsRowViewModel(string Label, long TotalTokens, string TotalText, string ToolTip = "");
