namespace AiUsage.Stats;

/// <summary>
/// Which providers <see cref="StatsIndexer"/> can ever produce a row for. Codex and Claude both
/// keep a local, per-line token count in their own session files; Gemini/Antigravity stores
/// its conversations as undecoded binary and Copilot keeps no token counts at all, so
/// neither is ever indexed. The statistics window reads this list, not a hardcoded pair of ids, so
/// the "keeps no token counts" line is proven against the same source the indexer itself
/// honours instead of a second, hand-kept copy of the same fact.
/// </summary>
public static class ProviderCoverage
{
    public static readonly IReadOnlyList<string> IndexedProviderIds = [StatsIndexer.ClaudeProviderId, StatsIndexer.CodexProviderId];

    /// <summary>All five providers this app knows, in the order the statistics window lists them -
    /// same order the main tile list itself uses (Claude, Codex, Cursor, Gemini, Copilot).</summary>
    public static readonly IReadOnlyList<string> AllProviderIds = ["claude", "codex", "cursor", "gemini", "copilot"];

    /// <summary>True for a provider <see cref="StatsIndexer"/> can ever have written a row for.</summary>
    public static bool HasLocalTokenData(string providerId) => IndexedProviderIds.Contains(providerId);
}
