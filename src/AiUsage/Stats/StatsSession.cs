namespace AiUsage.Stats;

/// <summary>
/// What one index walk found out about one session: the tokens it added, the span of time they fall
/// in, how much of it came from subagents and how the models split. Additive like <see
/// cref="StatsRecord"/> - the store adds it to whatever the session already holds. Like the usage
/// record it carries only labels, counts and times, never message text.
/// </summary>
public sealed record StatsSessionDelta(
    string Provider,
    string SessionId,
    string Project,
    DateTimeOffset FirstUtc,
    DateTimeOffset LastUtc,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long SubagentTokens,
    IReadOnlyDictionary<string, long> ModelTokens,
    string Machine = "")
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>True when part of the delta came from the main agent: only then does its project
    /// replace one an earlier delta (possibly a subagent's) already stored.</summary>
    public bool HasMainThreadTokens => SubagentTokens < TotalTokens;
}

/// <summary>One stored session of the token index: its totals across the main agent and every
/// subagent, the project it ran in and the model that carried most of it.</summary>
public sealed record StatsSessionRecord(
    string Provider,
    string Machine,
    string SessionId,
    string Project,
    DateTimeOffset FirstUtc,
    DateTimeOffset LastUtc,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    string MainModel,
    long SubagentTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>The local calendar day the session started on, the day the window's range filter
    /// judges it by.</summary>
    public DateOnly StartDay => DateOnly.FromDateTime(FirstUtc.ToLocalTime().DateTime);

    public TimeSpan Duration => LastUtc - FirstUtc;
}

/// <summary>Collects the usage lines of one file (or of a whole backfill) into per-session deltas.</summary>
internal sealed class StatsSessionAccumulator
{
    private sealed class Entry
    {
        public required string Provider { get; init; }
        public required string SessionId { get; init; }
        public string Project { get; set; } = "";
        public bool ProjectFromMainThread { get; set; }
        public DateTimeOffset First { get; set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset Last { get; set; } = DateTimeOffset.MinValue;
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheCreation { get; set; }
        public long CacheRead { get; set; }
        public long Subagent { get; set; }
        public Dictionary<string, long> Models { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<(string Provider, string SessionId), Entry> _entries = [];

    public bool IsEmpty => _entries.Count == 0;

    public void Add(
        string provider, string sessionId, string project, DateTimeOffset timestamp, string model, bool subagent,
        long input, long output, long cacheCreation, long cacheRead)
    {
        if (!_entries.TryGetValue((provider, sessionId), out var entry))
            _entries[(provider, sessionId)] = entry = new Entry { Provider = provider, SessionId = sessionId };

        // A main agent line decides the project; a subagent line only fills it in while nothing else did.
        if (!subagent && !entry.ProjectFromMainThread)
        {
            entry.Project = project;
            entry.ProjectFromMainThread = true;
        }
        else if (entry.Project.Length == 0)
        {
            entry.Project = project;
        }

        var utc = timestamp.ToUniversalTime();
        if (utc < entry.First)
            entry.First = utc;
        if (utc > entry.Last)
            entry.Last = utc;
        entry.Input += input;
        entry.Output += output;
        entry.CacheCreation += cacheCreation;
        entry.CacheRead += cacheRead;
        var total = input + output + cacheCreation + cacheRead;
        if (subagent)
            entry.Subagent += total;
        entry.Models[model] = entry.Models.GetValueOrDefault(model) + total;
    }

    public List<StatsSessionDelta> ToDeltas() =>
        [.. _entries.Values.Select(entry => new StatsSessionDelta(
            entry.Provider, entry.SessionId, entry.Project, entry.First, entry.Last,
            entry.Input, entry.Output, entry.CacheCreation, entry.CacheRead, entry.Subagent, entry.Models))];
}
