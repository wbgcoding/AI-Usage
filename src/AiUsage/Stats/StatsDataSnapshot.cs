namespace AiUsage.Stats;

/// <summary>
/// Everything the statistics window reads from the store, already prepared: the usage rows with bare
/// project names resolved, the sessions, and which project paths are one project that moved. <see cref="StatsIndexerService"/> keeps the latest one in
/// memory after each walk, so a window that opens has its data at once and only checks afterwards
/// whether anything moved.
/// </summary>
public sealed record StatsDataSnapshot(
    IReadOnlyList<StatsRecord> Records, IReadOnlyList<StatsSessionRecord> Sessions, ProjectAliases? Aliases = null)
{
    /// <summary>Reads the store the same way the view model's own load does.</summary>
    public static StatsDataSnapshot Load(StatsStore store) =>
        Prepared(LoadRecords(store), store.LoadSessions());

    /// <summary>Every usage row of the store with bare project names resolved: the one way the rows are
    /// read for display and export.</summary>
    internal static IReadOnlyList<StatsRecord> LoadRecords(StatsStore store) =>
        StatsAggregator.ResolveBareProjectNames(store.LoadAll());

    /// <summary>The snapshot with its project aliases worked out (touches the file system: call off the UI thread).</summary>
    public static StatsDataSnapshot Prepared(IReadOnlyList<StatsRecord> records, IReadOnlyList<StatsSessionRecord> sessions) =>
        new(records, sessions, ProjectAliasResolver.Build(records, sessions));
}
