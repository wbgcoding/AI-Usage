namespace AiUsage.Stats;

/// <summary>One row of the statistics table - already formatted for display, so the view stays a
/// plain binding with no value conversion of its own. <see cref="ToolTip"/> is empty for every row
/// whose <see cref="Label"/> is already the whole story (a day, a week, a provider, a model); a
/// project row that had its own full path shortened down to <see cref="Label"/> carries that full
/// path back here instead, so a hover can still name it exactly. <see cref="CostUsd"/> is the row's
/// cost in plain dollars for the export, empty when unknown.</summary>
public sealed record StatsRowViewModel(
    string Label, long TotalTokens, string TotalText, string ToolTip = "",
    StatsFilterKind FilterKind = StatsFilterKind.None, string FilterKey = "",
    string CostUsd = "");

/// <summary>What a click on a model or a project narrows every figure to.</summary>
public enum StatsFilterKind
{
    None,
    Model,
    Project,
}
