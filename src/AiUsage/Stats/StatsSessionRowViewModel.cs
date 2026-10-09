namespace AiUsage.Stats;

/// <summary>One row of the session table, already formatted for display: the five columns and the
/// hover text that names the session in full.</summary>
public sealed record StatsSessionRowViewModel(
    string Project, string Start, string Duration, string Tokens, string Model, string ToolTip);
