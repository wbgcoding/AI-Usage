namespace AiUsage.Stats;

/// <summary>
/// What <see cref="StatsIndexer"/> remembers about one already-read source file, so the next run
/// only reads what was appended since - a file whose size and write time are unchanged is skipped
/// outright, and a grown file is read from <see cref="Offset"/> onward rather than from the start.
///
/// The four cumulative fields exist only for a provider (Codex) whose own source file carries a
/// running total rather than a per-line delta: the parser turns that total into a delta by
/// subtracting the previous line's total, and a resumed read needs that previous total to carry on
/// correctly instead of restarting from zero and under counting. A provider that already reports a
/// delta per line (Claude) simply leaves all four at zero and instead keeps
/// <see cref="LastMessageKey"/>, the last response seen, together with the tokens already counted
/// for it, so a resumed read neither counts a repeated copy of it again nor misses the part a later,
/// fuller copy adds. A row written before those counts were kept holds the key with all four counts
/// unset.
/// </summary>
public sealed record StatsSourceFileState(
    string Path,
    string Provider,
    long Offset,
    long Size,
    DateTime WriteTimeUtc,
    string CurrentModel = "",
    long CumulativeInputTokens = 0,
    long CumulativeOutputTokens = 0,
    long CumulativeCacheCreationTokens = 0,
    long CumulativeCacheReadTokens = 0,
    string CurrentEffort = "",
    string LastMessageKey = "",
    long? CountedInputTokens = null,
    long? CountedOutputTokens = null,
    long? CountedCacheCreationTokens = null,
    long? CountedCacheReadTokens = null)
{
    /// <summary>The tokens counted so far for <see cref="LastMessageKey"/>; null when a row from an
    /// earlier build did not keep them.</summary>
    public ClaudeCopyTokens? CountedForLastMessage =>
        CountedInputTokens is { } input && CountedOutputTokens is { } output
            && CountedCacheCreationTokens is { } cacheCreation && CountedCacheReadTokens is { } cacheRead
            ? new ClaudeCopyTokens(input, output, cacheCreation, cacheRead)
            : null;

    /// <summary>How long a file's write time must stay unchanged before a trailing line with no
    /// newline yet is trusted to be complete rather than still being appended to.</summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromSeconds(60);

    /// <summary>True once a file's size and write time both match what was stored last time - the
    /// cheap check <see cref="StatsIndexer"/> makes before ever opening the file again. <see
    /// cref="Offset"/> landing short of <see cref="Size"/> means the last read left an unterminated
    /// trailing line behind on purpose: that alone must not keep matching forever, or the
    /// line would never be read once the writer stops appending to the file at all - once <paramref
    /// name="now"/> is past <see cref="SettlingWindow"/> since the recorded write time, the file no
    /// longer matches, so the indexer opens it once more and the trailing line finally gets its
    /// chance to be accepted as complete.</summary>
    public bool MatchesOnDisk(long size, DateTime writeTimeUtc, DateTime now)
    {
        if (Size != size || WriteTimeUtc != writeTimeUtc)
            return false;

        return Offset >= Size || now - WriteTimeUtc < SettlingWindow;
    }
}

/// <summary>The four token counts of one copy of a Claude response (or what is counted for it).</summary>
public readonly record struct ClaudeCopyTokens(long Input, long Output, long CacheCreation, long CacheRead)
{
    /// <summary>The part of this copy that exceeds <paramref name="counted"/>, per field, never negative.</summary>
    public ClaudeCopyTokens ExcessOver(ClaudeCopyTokens counted) => new(
        Math.Max(0, Input - counted.Input), Math.Max(0, Output - counted.Output),
        Math.Max(0, CacheCreation - counted.CacheCreation), Math.Max(0, CacheRead - counted.CacheRead));

    public ClaudeCopyTokens MaxWith(ClaudeCopyTokens other) => new(
        Math.Max(Input, other.Input), Math.Max(Output, other.Output),
        Math.Max(CacheCreation, other.CacheCreation), Math.Max(CacheRead, other.CacheRead));

    public bool IsZero => Input == 0 && Output == 0 && CacheCreation == 0 && CacheRead == 0;
}
