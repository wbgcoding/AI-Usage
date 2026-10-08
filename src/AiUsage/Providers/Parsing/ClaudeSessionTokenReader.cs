using AiUsage.Io;
using AiUsage.Stats;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Sums a Claude Code session transcript's own token counts, incrementally: the class remembers the
/// file it last read, how far into it, and the sum reached by then, so a call against the SAME
/// still-growing file only reads the bytes appended since the last call instead of the whole
/// transcript again. A shorter file, or a different path, is read from the very start - either means
/// the append-only assumption this shortcut relies on no longer holds.
/// </summary>
public sealed class ClaudeSessionTokenReader
{
    private string? _lastPath;
    private long _lastSum;

    // Claude Code writes one response several times (same message id and request id); the pair is
    // counted once, kept across incremental reads of the same file and dropped with it.
    private readonly HashSet<string> _seenMessages = new(StringComparer.Ordinal);

    // A single line longer than this is never a usage line; it is skipped without being buffered.
    private const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>The timestamp of the newest usage line summed so far in the current file - null before
    /// any was read. A session that last spoke hours ago is not the running one, whatever its sum.</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>The file length this reader had summed up to as of its last call - internal purely so
    /// the test project (<c>InternalsVisibleTo</c> in <c>AiUsage.csproj</c>) can prove a second call
    /// against an appended file reads only the new bytes; production code never reads this itself.</summary>
    internal long LastReadOffset { get; private set; }

    /// <summary>Reads <paramref name="sessionFile"/> once, feeding every line through <see
    /// cref="ClaudeUsageLogParser.TryParse"/> and adding its four token counts to the running total -
    /// or, on a repeat call against the same file, only the lines appended since the last call.</summary>
    public long SumTokens(string sessionFile)
    {
        var length = new FileInfo(sessionFile).Length;

        if (sessionFile != _lastPath || length < LastReadOffset)
        {
            _lastPath = sessionFile;
            _lastSum = 0;
            LastReadOffset = 0;
            LastEventAt = null;
            _seenMessages.Clear();
        }

        using (var stream = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // Only newline-terminated lines are consumed: the resume offset ends at the last one, so a
            // line the writer has not finished yet is read whole on the next call instead of being
            // cut in two and lost. Growth after the stream was opened is the next call's business.
            LastReadOffset = CompleteLineReader.Read(stream, LastReadOffset, false, MaxLineBytes, () => { }, line =>
            {
                if (!ClaudeUsageLogParser.TryParse(line, out var usage))
                    return;
                if (usage.MessageKey.Length > 0 && !_seenMessages.Add(usage.MessageKey))
                    return;

                if (LastEventAt is not { } newest || usage.Timestamp > newest)
                    LastEventAt = usage.Timestamp;
                _lastSum += usage.InputTokens + usage.OutputTokens + usage.CacheCreationTokens + usage.CacheReadTokens;
            }, CancellationToken.None);
        }

        return _lastSum;
    }
}
