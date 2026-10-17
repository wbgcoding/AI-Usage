using AiUsage.Io;
using AiUsage.Stats;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Sums a Claude Code session transcript's own token counts, incrementally: for each of the last few
/// files it read, the class remembers how far into the file it got and the sum reached by then, so a
/// call against a still-growing file only reads the bytes appended since that file's last call
/// instead of the whole transcript again. Two sessions open at once take turns as the newest file;
/// keeping a state per file means neither of them is re-read from the start on every switch. A
/// shorter file means the append-only assumption this shortcut relies on no longer holds, so that
/// file is read from the very start again.
/// </summary>
public sealed class ClaudeSessionTokenReader
{
    // Files remembered at once; the least recently read one is dropped first.
    private const int MaxTrackedFiles = 4;

    private sealed class FileState
    {
        public long Sum;
        public long Offset;
        public DateTimeOffset? LastEventAt;

        // Claude Code writes one response several times (same message id and request id); the pair is
        // counted once, kept across incremental reads of the same file and dropped with it.
        public readonly HashSet<string> SeenMessages = new(StringComparer.Ordinal);
    }

    // Oldest read first, newest last.
    private readonly List<(string Path, FileState State)> _states = [];

    // A read abandoned by a timed-out refresh may still be running when the retry starts; the two must
    // never update one file's sum at the same time.
    private readonly Lock _gate = new();
    private FileState? _current;

    // A single line longer than this is never a usage line; it is skipped without being buffered.
    private const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>The timestamp of the newest usage line summed so far in the file of the last call - null
    /// before any was read. A session that last spoke hours ago is not the running one, whatever its
    /// sum.</summary>
    public DateTimeOffset? LastEventAt => _current?.LastEventAt;

    /// <summary>The file length the last call had summed up to - internal purely so the test project
    /// (<c>InternalsVisibleTo</c> in <c>AiUsage.csproj</c>) can prove a second call against an appended
    /// file reads only the new bytes; production code never reads this itself.</summary>
    internal long LastReadOffset => _current?.Offset ?? 0;

    /// <summary>All bytes consumed over this reader's lifetime - internal for the same reason as
    /// <see cref="LastReadOffset"/>: a test proves that going back to a file read earlier does not
    /// read it from its start again.</summary>
    internal long TotalBytesRead { get; private set; }

    /// <summary>Reads <paramref name="sessionFile"/> once, feeding every line through <see
    /// cref="ClaudeUsageLogParser.TryParse"/> and adding its four token counts to that file's running
    /// total - or, on a repeat call against the same file, only the lines appended since the last
    /// call.</summary>
    public long SumTokens(string sessionFile, CancellationToken ct = default)
    {
        lock (_gate)
            return SumTokensCore(sessionFile, ct);
    }

    private long SumTokensCore(string sessionFile, CancellationToken ct)
    {
        var length = new FileInfo(sessionFile).Length;
        var state = StateFor(sessionFile);

        if (length < state.Offset)
            Reset(state);

        var offsetBefore = state.Offset;
        try
        {
            using var stream = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Only newline-terminated lines are consumed: the resume offset ends at the last one, so a
            // line the writer has not finished yet is read whole on the next call instead of being
            // cut in two and lost. Growth after the stream was opened is the next call's business.
            state.Offset = CompleteLineReader.Read(stream, state.Offset, false, MaxLineBytes, () => { }, line =>
            {
                if (!ClaudeUsageLogParser.TryParse(line, out var usage))
                    return;
                if (usage.MessageKey.Length > 0 && !state.SeenMessages.Add(usage.MessageKey))
                    return;

                if (state.LastEventAt is not { } newest || usage.Timestamp > newest)
                    state.LastEventAt = usage.Timestamp;
                state.Sum += usage.InputTokens + usage.OutputTokens + usage.CacheCreationTokens + usage.CacheReadTokens;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // The lines summed before the cancel are past the stored offset; the file is read whole
            // next time rather than counted twice.
            Reset(state);
            throw;
        }

        TotalBytesRead += state.Offset - offsetBefore;
        return state.Sum;
    }

    private static void Reset(FileState state)
    {
        state.Sum = 0;
        state.Offset = 0;
        state.LastEventAt = null;
        state.SeenMessages.Clear();
    }

    /// <summary>The state of <paramref name="path"/>, moved to the newest position; a new file takes the
    /// place of the least recently read one when all slots are in use.</summary>
    private FileState StateFor(string path)
    {
        var index = _states.FindIndex(entry => entry.Path == path);
        if (index >= 0)
        {
            var entry = _states[index];
            _states.RemoveAt(index);
            _states.Add(entry);
            return _current = entry.State;
        }

        if (_states.Count >= MaxTrackedFiles)
            _states.RemoveAt(0);

        var state = new FileState();
        _states.Add((path, state));
        return _current = state;
    }
}
