namespace AiUsage.Stats;

/// <summary>
/// One summed row of the token usage index: a provider's token counts for one day, one hour of that
/// day, one model and one project. Additive by design - two records with the same key (<see
/// cref="Provider"/>, <see cref="Day"/>, <see cref="Hour"/>, <see cref="Model"/>, <see
/// cref="Project"/>) are meant to be summed together, never replaced, so a re-read of a file that
/// grew since the last index run only ever adds to what is already stored.
///
/// Deliberately narrow: every field is either a short label (provider id, model name, project name),
/// a plain token count, or the day/hour a source line's own timestamp fell on. Nothing here can ever
/// hold message content - a source line's actual text is never read into this row in the first
/// place, so there is no field that could carry it even by accident. <c>TokenSafetyTests</c> proves
/// this against the field list itself, not just this comment.
/// </summary>
public sealed record StatsRecord(
    string Provider,
    DateOnly Day,
    string Model,
    string Project,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    int Hour = 0,
    string Effort = "")
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>Two records combine by adding every token count - the key fields must already match,
    /// callers group by (<see cref="Provider"/>, <see cref="Day"/>, <see cref="Model"/>, <see
    /// cref="Project"/>) before calling this.</summary>
    public StatsRecord Add(StatsRecord other) => this with
    {
        InputTokens = InputTokens + other.InputTokens,
        OutputTokens = OutputTokens + other.OutputTokens,
        CacheCreationTokens = CacheCreationTokens + other.CacheCreationTokens,
        CacheReadTokens = CacheReadTokens + other.CacheReadTokens,
    };
}
