namespace AiUsage.Stats;

/// <summary>
/// Everything the statistics window reads from the store, already prepared: the usage rows with bare
/// project names resolved, and the sessions. <see cref="StatsIndexerService"/> keeps the latest one in
/// memory after each walk, so a window that opens has its data at once and only checks afterwards
/// whether anything moved.
/// </summary>
public sealed record StatsDataSnapshot(IReadOnlyList<StatsRecord> Records, IReadOnlyList<StatsSessionRecord> Sessions)
{
    /// <summary>Reads the store the same way the view model's own load does.</summary>
    public static StatsDataSnapshot Load(StatsStore store) =>
        new(StatsAggregator.ResolveBareProjectNames(store.LoadAll()), store.LoadSessions());
}
