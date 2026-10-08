namespace AiUsage.Stats;

/// <summary>One row of the "every provider" list: a display name, its token total shortened for the
/// row and the same total written out in full for the tooltip, already resolved so the view stays a
/// plain binding with no branching of its own.</summary>
public sealed record StatsProviderRowViewModel(string DisplayName, string ValueText, string ExactText);
