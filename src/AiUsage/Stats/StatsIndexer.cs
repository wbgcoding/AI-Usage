using System.Text;
using AiUsage.Io;
using AiUsage.Providers;
using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>What one <see cref="StatsIndexer.IndexOnce"/> call actually did, per provider - the
/// number behind "how much of the real usage does this index actually cover", which nothing
/// reported before this existed. A line the walk chose not to look at (a file skipped because its
/// size and write time already matched what was stored) counts toward neither <see
/// cref="StatsIndexCounters.LinesParsed"/> nor <see cref="StatsIndexCounters.LinesSkipped"/> - only
/// lines an actually opened file yields land in either bucket.</summary>
public readonly record struct StatsIndexResult(StatsIndexCounters Claude, StatsIndexCounters Codex);

/// <summary><see cref="FilesSeen"/> is every matching file the walk found under a provider's
/// root(s), whether or not it ended up being opened. <see cref="FilesRead"/> is the subset that was
/// actually opened this run (a file whose size and write time already matched what was stored is
/// seen but never read). <see cref="LinesParsed"/> and <see cref="LinesSkipped"/> only count lines
/// from files that were read: a recognised usage line versus one that could not be parsed or carried
/// no usage. <see cref="FoldersSkipped"/> is every subfolder under the provider's root(s) this walk
/// could not list at all (permissions, a broken link, …) - whatever files it holds are missing from
/// every other count above without a trace, unless this number says so.</summary>
public readonly record struct StatsIndexCounters(int FilesSeen, int FilesRead, long LinesParsed, long LinesSkipped, int FoldersSkipped);

/// <summary>
/// Walks every Claude and Codex session log on this machine and folds whatever is new since the
/// last run into <see cref="StatsStore"/>. A single <see cref="IndexOnce"/> call is the whole
/// contract: safe to call repeatedly (a file whose size and write time match what is already stored
/// is skipped without being opened), safe to cancel mid-walk (already-finished files stay
/// finished - the next call simply continues with whatever is left), and never throws (a file that
/// vanishes or locks up mid-read is left for the next run rather than failing the whole walk).
///
/// Gemini/Antigravity and Copilot are not walked here at all: neither keeps a local, per-line token
/// count to index - <see cref="ProviderCoverage"/> is what the statistics window itself
/// reads to say so.
/// </summary>
public sealed class StatsIndexer
{
    public const string ClaudeProviderId = "claude";
    public const string CodexProviderId = "codex";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StatsStore _store;
    private readonly IReadOnlyList<string> _claudeProjectsRoots;
    private readonly string _codexSessionsRoot;
    private readonly string? _codexArchivedRoot;
    private readonly Func<DateTime> _clock;

    /// <summary>The longest single JSONL record read into memory. A record past this (a corrupt or
    /// runaway line) is skipped up to its next newline instead of being buffered.</summary>
    internal const int DefaultMaxRecordBytes = 32 * 1024 * 1024;

    internal int MaxRecordBytes { get; init; } = DefaultMaxRecordBytes;

    internal LogService? Log { get; init; }

    public StatsIndexer(StatsStore store, LogService? logService = null)
        : this(store, DefaultClaudeProjectsRoots(), DefaultCodexSessionsRoot, DefaultCodexArchivedRoot, () => DateTime.UtcNow)
    {
        Log = logService;
    }

    /// <summary>Test seam: fake roots instead of the real <c>~/.claude/projects</c> and
    /// <c>~/.codex/sessions</c>, so a test walk never touches this machine's own session history.</summary>
    internal StatsIndexer(StatsStore store, IReadOnlyList<string> claudeProjectsRoots, string codexSessionsRoot)
        : this(store, claudeProjectsRoots, codexSessionsRoot, () => DateTime.UtcNow)
    {
    }

    /// <summary>Same seam, for a test that only cares about a single Claude root.</summary>
    internal StatsIndexer(StatsStore store, string claudeProjectsRoot, string codexSessionsRoot)
        : this(store, [claudeProjectsRoot], codexSessionsRoot)
    {
    }

    /// <summary>Same seam, for a test that only cares about a single Claude root and also needs to
    /// control the clock behind the settling window.</summary>
    internal StatsIndexer(StatsStore store, string claudeProjectsRoot, string codexSessionsRoot, Func<DateTime> clock)
        : this(store, [claudeProjectsRoot], codexSessionsRoot, clock)
    {
    }

    /// <summary>Test seam: an injectable clock instead of <see cref="DateTime.UtcNow"/>, so a test
    /// can prove the settling-window behaviour without an actual 60 s wait.</summary>
    internal StatsIndexer(StatsStore store, IReadOnlyList<string> claudeProjectsRoots, string codexSessionsRoot, Func<DateTime> clock)
        : this(store, claudeProjectsRoots, codexSessionsRoot, null, clock)
    {
    }

    /// <summary>Test seam for the folder Codex moves finished sessions into; the other seams leave it
    /// unset (no archive walked), so a test never reads this machine's real archive.</summary>
    internal StatsIndexer(StatsStore store, string claudeProjectsRoot, string codexSessionsRoot, string codexArchivedRoot)
        : this(store, [claudeProjectsRoot], codexSessionsRoot, codexArchivedRoot, () => DateTime.UtcNow)
    {
    }

    internal StatsIndexer(
        StatsStore store, IReadOnlyList<string> claudeProjectsRoots, string codexSessionsRoot, string? codexArchivedRoot, Func<DateTime> clock)
    {
        _store = store;
        _claudeProjectsRoots = claudeProjectsRoots;
        _codexSessionsRoot = codexSessionsRoot;
        _codexArchivedRoot = codexArchivedRoot;
        _clock = clock;
    }

    /// <summary>Every Claude <c>projects</c> folder this machine actually has, from the shared
    /// config-root list (<see cref="ClaudeConfigRoot"/>): only existing folders survive,
    /// deduplicated by full path, so a machine with just the classic layout walks exactly one.</summary>
    private static List<string> DefaultClaudeProjectsRoots() =>
        ClaudeConfigRoot.ExistingProjectsRoots(ClaudeConfigRoot.Candidates());

    /// <summary>Same as the default, from a given variable value and profile folder.</summary>
    internal static List<string> ClaudeProjectsRootsFor(string? configDirValue, string userProfile) =>
        ClaudeConfigRoot.ExistingProjectsRoots(ClaudeConfigRoot.Candidates(configDirValue, userProfile));

    private static string DefaultCodexSessionsRoot => Providers.CodexPaths.Sessions;

    private static string DefaultCodexArchivedRoot => Providers.CodexPaths.ArchivedSessions;

    public StatsIndexResult IndexOnce(CancellationToken cancellationToken = default)
    {
        var claudeFilesSeen = 0;
        var claudeFilesRead = 0;
        var claudeLinesParsed = 0L;
        var claudeLinesSkipped = 0L;
        var claudeFoldersSkipped = 0;

        // One query for every remembered file instead of one database open per file: most walks find
        // thousands of files and change none of them.
        var known = _store.LoadSourceFiles();
        BackfillSessionsOnce(known, cancellationToken);

        foreach (var claudeRoot in _claudeProjectsRoots)
        {
            var claudeFiles = EnumerateNewestFirstWithInfo(claudeRoot, "*.jsonl", out var foldersSkipped, cancellationToken);
            claudeFoldersSkipped += foldersSkipped;

            foreach (var listed in claudeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                claudeFilesSeen++;
                var (read, linesParsed, linesSkipped) = IndexClaudeFile(listed, known, cancellationToken);
                if (read)
                    claudeFilesRead++;
                claudeLinesParsed += linesParsed;
                claudeLinesSkipped += linesSkipped;
            }
        }

        var codexFilesSeen = 0;
        var codexFilesRead = 0;
        var codexLinesParsed = 0L;
        var codexLinesSkipped = 0L;

        var codexFiles = EnumerateNewestFirstWithInfo(_codexSessionsRoot, "rollout-*.jsonl", out var codexFoldersSkipped, cancellationToken);
        if (_codexArchivedRoot is not null)
        {
            // Codex moves a finished session out of the sessions tree; its usage still counts.
            codexFiles = [.. codexFiles, .. EnumerateNewestFirstWithInfo(_codexArchivedRoot, "rollout-*.jsonl", out var archivedFoldersSkipped, cancellationToken)];
            codexFoldersSkipped += archivedFoldersSkipped;
        }

        foreach (var listed in codexFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            codexFilesSeen++;
            var (read, linesParsed, linesSkipped) = IndexCodexFile(listed, known, cancellationToken);
            if (read)
                codexFilesRead++;
            codexLinesParsed += linesParsed;
            codexLinesSkipped += linesSkipped;
        }

        return new StatsIndexResult(
            new StatsIndexCounters(claudeFilesSeen, claudeFilesRead, claudeLinesParsed, claudeLinesSkipped, claudeFoldersSkipped),
            new StatsIndexCounters(codexFilesSeen, codexFilesRead, codexLinesParsed, codexLinesSkipped, codexFoldersSkipped));
    }

    /// <summary>Every matching file under <paramref name="root"/>, newest write time first - so a
    /// window opened while the very first walk is still running already shows the newest days
    /// instead of whatever the filesystem happened to enumerate first. One unreadable
    /// subdirectory never hides every other file under <paramref name="root"/>: each subdirectory is
    /// listed on its own, and one that throws is counted into <paramref name="foldersSkipped"/>
    /// rather than failing the whole walk or vanishing without a trace the way blanket <see
    /// cref="EnumerationOptions.IgnoreInaccessible"/> used to.</summary>
    internal static IReadOnlyList<string> EnumerateNewestFirst(
        string root, string searchPattern, out int foldersSkipped, CancellationToken cancellationToken = default) =>
        EnumerateNewestFirstWithInfo(root, searchPattern, out foldersSkipped, cancellationToken)
            .Select(info => info.FullName)
            .ToList();

    /// <summary>The same walk, keeping the listing's entries. Their size and write time are only good
    /// for ordering: a file a writer still holds open is listed with stale values, so every file is
    /// stat'ed afresh before it is judged.</summary>
    private static List<FileInfo> EnumerateNewestFirstWithInfo(
        string root, string searchPattern, out int foldersSkipped, CancellationToken cancellationToken)
    {
        foldersSkipped = 0;
        if (!Directory.Exists(root))
            return [];

        var files = new List<FileInfo>();
        var skipped = 0;
        WalkDirectory(root, searchPattern, files, ref skipped, cancellationToken);
        foldersSkipped = skipped;

        files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        return files;
    }

    /// <summary>Same as <see cref="EnumerateNewestFirst(string, string, out int)"/> for a caller that
    /// does not care how many subdirectories were unreadable.</summary>
    internal static IReadOnlyList<string> EnumerateNewestFirst(string root, string searchPattern) =>
        EnumerateNewestFirst(root, searchPattern, out _);

    private static void WalkDirectory(
        string root, string searchPattern, List<FileInfo> files, ref int foldersSkipped, CancellationToken cancellationToken)
    {
        // An explicit stack instead of recursion, and no reparse points below the root: a junction or
        // symlink cycle must neither overflow the stack nor be walked forever.
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            List<string> subdirectories = [];
            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                foreach (var file in directoryInfo.EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }

                foreach (var subdirectory in directoryInfo.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        subdirectories.Add(subdirectory.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foldersSkipped++;
                continue;
            }

            // Reversed so the pops come out in the listing's own order.
            for (var i = subdirectories.Count - 1; i >= 0; i--)
                pending.Push(subdirectories[i]);
        }
    }

    private (bool Read, long LinesParsed, long LinesSkipped) IndexClaudeFile(
        FileInfo listed, Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        var path = listed.FullName;
        var info = TryStat(path);
        if (info is null)
            return (false, 0, 0);

        if (!TryFindState(path, known, out var existing))
            return (false, 0, 0);
        if (existing is not null && existing.MatchesOnDisk(info.Length, info.LastWriteTimeUtc, _clock()))
            return (false, 0, 0);

        if (existing is not null && info.Length < existing.Size)
        {
            // Shrank or was replaced since the last run. Its old contribution is already counted and
            // cannot be traced back out, so reading the new content from the start would count the
            // same usage twice. The file is left unread and only its remembered size and write time
            // move to what is on disk now, so a later append is picked up from the new end.
            _store.SetSourceFile(existing with { Offset = info.Length, Size = info.Length, WriteTimeUtc = info.LastWriteTimeUtc });
            return (false, 0, 0);
        }

        var offset = existing?.Offset ?? 0;
        var countedKeys = new HashSet<string>(StringComparer.Ordinal);
        var lastMessageKey = existing?.LastMessageKey ?? "";
        if (lastMessageKey.Length > 0)
            countedKeys.Add(lastMessageKey);
        var project = ClaudeUsageLogParser.ExtractProjectFromFilePath(path);
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var fallbackSessionId = ClaudeUsageLogParser.FallbackSessionId(path);
        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        var sessions = new StatsSessionAccumulator();
        var acceptTrailingLineWithoutNewline = _clock() - info.LastWriteTimeUtc >= StatsSourceFileState.SettlingWindow;
        long consumedOffset;
        long openLength;
        var linesParsed = 0L;
        var linesSkipped = 0L;

        try
        {
            project = ProbeClaudeProject(path, project, cancellationToken);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            openLength = stream.Length;
            consumedOffset = CompleteLineReader.Read(stream, offset, acceptTrailingLineWithoutNewline, MaxRecordBytes, LogOversizedRecord, line =>
            {
                if (ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                {
                    // The same response is written more than once; only its first copy counts.
                    if (usageEvent.MessageKey.Length > 0)
                    {
                        if (!countedKeys.Add(usageEvent.MessageKey))
                        {
                            linesSkipped++;
                            return;
                        }

                        lastMessageKey = usageEvent.MessageKey;
                    }

                    linesParsed++;
                    var subagent = fileIsSubagent || usageEvent.IsSidechain;
                    Accumulate(buckets, ClaudeProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                        usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens,
                        usageEvent.Effort, subagent);
                    sessions.Add(ClaudeProviderId, usageEvent.SessionId.Length > 0 ? usageEvent.SessionId : fallbackSessionId, project,
                        usageEvent.Timestamp, usageEvent.Model, subagent,
                        usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens);
                }
                else
                {
                    linesSkipped++;
                }
            }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), () => linesSkipped++);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, 0, 0); // left exactly as it was - retried next run
        }

        _store.ApplyIndexResult(
            [.. buckets.Values],
            new StatsSourceFileState(
                path, ClaudeProviderId, consumedOffset, openLength, info.LastWriteTimeUtc, LastMessageKey: lastMessageKey),
            sessions.ToDeltas());
        return (true, linesParsed, linesSkipped);
    }

    private (bool Read, long LinesParsed, long LinesSkipped) IndexCodexFile(
        FileInfo listed, Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        var path = listed.FullName;
        var info = TryStat(path);
        if (info is null)
            return (false, 0, 0);

        // A session Codex moved into the archive keeps its name: its old row (path gone from disk)
        // is taken over so the file resumes where it stood instead of being counted from the start.
        if (!TryFindState(path, known, out var existing))
            return (false, 0, 0);
        existing ??= _store.TryAdoptMovedSourceFile(path, CodexProviderId);
        if (existing is not null && existing.MatchesOnDisk(info.Length, info.LastWriteTimeUtc, _clock()))
            return (false, 0, 0);

        if (existing is not null && info.Length < existing.Size)
        {
            // Same as for a shrunk Claude file: not read again, only its size and write time move.
            _store.SetSourceFile(existing with { Offset = info.Length, Size = info.Length, WriteTimeUtc = info.LastWriteTimeUtc });
            return (false, 0, 0);
        }

        var offset = existing?.Offset ?? 0;
        var parser = new CodexUsageLogParser(
            model: existing?.CurrentModel ?? "",
            cumulativeInput: existing?.CumulativeInputTokens ?? 0,
            cumulativeOutput: existing?.CumulativeOutputTokens ?? 0,
            cumulativeCacheCreation: existing?.CumulativeCacheCreationTokens ?? 0,
            cumulativeCacheRead: existing?.CumulativeCacheReadTokens ?? 0,
            effort: existing?.CurrentEffort ?? "");

        var project = "";
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        var sessions = new StatsSessionAccumulator();
        var acceptTrailingLineWithoutNewline = _clock() - info.LastWriteTimeUtc >= StatsSourceFileState.SettlingWindow;
        long consumedOffset;
        long openLength;
        var linesParsed = 0L;
        var linesSkipped = 0L;

        try
        {
            // The project lives in the file's own first line (a "session_meta" event), read here on
            // every run regardless of the resume offset - the alternative, persisting it alongside
            // the resume state, would need its own migration path the moment a second field like it
            // was ever needed. Re-reading one short line is cheap next to walking the rest of the file.
            using (var probeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var probeReader = new StreamReader(probeStream, Utf8NoBom))
            {
                var firstLine = probeReader.ReadLine();
                if (firstLine is not null)
                {
                    project = CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(firstLine) ?? "";
                    sessionId = CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(firstLine) ?? sessionId;
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            openLength = stream.Length;
            consumedOffset = CompleteLineReader.Read(stream, offset, acceptTrailingLineWithoutNewline, MaxRecordBytes, LogOversizedRecord, line =>
            {
                if (parser.TryParseLine(line, out var usageEvent))
                {
                    linesParsed++;
                    Accumulate(buckets, CodexProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                        usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens,
                        usageEvent.Effort, subagent: false);
                    sessions.Add(CodexProviderId, sessionId, project, usageEvent.Timestamp, usageEvent.Model, subagent: false,
                        usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens);
                }
                else
                {
                    linesSkipped++;
                }
            }, cancellationToken, line => CodexUsageLogParser.MayAffectState(line), () => linesSkipped++);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, 0, 0);
        }

        var state = parser.State;
        _store.ApplyIndexResult(
            [.. buckets.Values],
            new StatsSourceFileState(
                path, CodexProviderId, consumedOffset, openLength, info.LastWriteTimeUtc,
                state.Model, state.CumulativeInput, state.CumulativeOutput, state.CumulativeCacheCreation, state.CumulativeCacheRead,
                state.Effort),
            sessions.ToDeltas());
        return (true, linesParsed, linesSkipped);
    }

    /// <summary>The one-time pass after a migration that left usage rows without sessions: every
    /// remembered file that still exists is read again, up to the offset the usage rows already
    /// cover, into session totals only. The usage rows and the file markers are not touched, so the
    /// sessions add up to the usage they belong to, and a file that grew meanwhile contributes its new
    /// part through the normal walk that follows. A file that is gone or shorter than its marker (it
    /// was replaced) stays out. Nothing is marked done when the pass is cancelled or cannot be
    /// written, so the next walk repeats it.</summary>
    private void BackfillSessionsOnce(Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        if (_store.IsSessionBackfillDone())
            return;

        var sessions = new StatsSessionAccumulator();
        foreach (var state in known.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Offset <= 0)
                continue;

            try
            {
                if (state.Provider == ClaudeProviderId)
                    BackfillClaudeFile(state, sessions, cancellationToken);
                else if (state.Provider == CodexProviderId)
                    BackfillCodexFile(state, sessions, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or is locked has no session data to give.
            }
        }

        _store.ReplaceSessionsAndMarkBackfilled(sessions.ToDeltas());
    }

    private void BackfillClaudeFile(StatsSourceFileState state, StatsSessionAccumulator sessions, CancellationToken cancellationToken)
    {
        var path = state.Path;
        var project = ProbeClaudeProject(path, ClaudeUsageLogParser.ExtractProjectFromFilePath(path), cancellationToken);
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var fallbackSessionId = ClaudeUsageLogParser.FallbackSessionId(path);
        var countedKeys = new HashSet<string>(StringComparer.Ordinal);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        CompleteLineReader.Read(stream, 0, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (!ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                return;
            if (usageEvent.MessageKey.Length > 0 && !countedKeys.Add(usageEvent.MessageKey))
                return;

            sessions.Add(ClaudeProviderId, usageEvent.SessionId.Length > 0 ? usageEvent.SessionId : fallbackSessionId, project,
                usageEvent.Timestamp, usageEvent.Model, fileIsSubagent || usageEvent.IsSidechain,
                usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens);
        }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), endLimit: state.Offset);
    }

    private void BackfillCodexFile(StatsSourceFileState state, StatsSessionAccumulator sessions, CancellationToken cancellationToken)
    {
        var path = state.Path;
        var project = "";
        var sessionId = Path.GetFileNameWithoutExtension(path);
        using (var probeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var probeReader = new StreamReader(probeStream, Utf8NoBom))
        {
            var firstLine = probeReader.ReadLine();
            if (firstLine is not null)
            {
                project = CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(firstLine) ?? "";
                sessionId = CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(firstLine) ?? sessionId;
            }
        }

        var parser = new CodexUsageLogParser();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        CompleteLineReader.Read(stream, 0, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (parser.TryParseLine(line, out var usageEvent))
            {
                sessions.Add(CodexProviderId, sessionId, project, usageEvent.Timestamp, usageEvent.Model, subagent: false,
                    usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens);
            }
        }, cancellationToken, line => CodexUsageLogParser.MayAffectState(line), endLimit: state.Offset);
    }

    /// <summary>How much of a transcript's start the project probe looks at.</summary>
    private const int ProbeBytes = 64 * 1024;

    /// <summary>The complete lines inside the first <paramref name="maxBytes"/> of the file: a line
    /// the cap cuts in two is dropped, so the probe never holds more than the cap in memory and never
    /// parses half a record.</summary>
    private static IEnumerable<string> ReadProbeLines(string path, int maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer;
        int read;
        bool capped;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            buffer = new byte[maxBytes];
            read = 0;
            while (read < maxBytes)
            {
                var n = stream.Read(buffer, read, maxBytes - read);
                if (n <= 0)
                    break;
                read += n;
            }

            capped = read == maxBytes && stream.Length > maxBytes;
        }

        var end = read;
        if (capped)
        {
            end = Array.LastIndexOf(buffer, (byte)'\n', read - 1) + 1;
            if (end <= 0)
                yield break;
        }

        var start = 0;
        while (start < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            var lineEnd = newline < 0 ? end : newline;
            yield return Utf8NoBom.GetString(buffer, start, lineEnd - start).TrimEnd('\r');
            start = lineEnd + 1;
        }
    }

    private void LogOversizedRecord() =>
        Log?.LogError("Statistics: a session record larger than the size limit was skipped.");

    /// <summary>The day and hour bucket are this machine's own LOCAL day/hour of <paramref
    /// name="timestamp"/>, not UTC - a session made at 23:30 local time in a zone ahead of UTC used to
    /// land on the next UTC day, and every hour in the "by hour" chart used to read one or two hours
    /// off in Germany (UTC+1/+2). <see cref="MainViewModel.RefreshWeekTokens"/> already computes its
    /// own week start in local time, so this makes the index consistent with it rather than the other
    /// way around.</summary>
    private static void Accumulate(
        Dictionary<UsageBucketKey, StatsRecord> buckets, string provider, string project,
        DateTimeOffset timestamp, string model, long input, long output, long cacheCreation, long cacheRead, string effort, bool subagent)
    {
        var local = timestamp.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var hour = local.Hour;
        var key = new UsageBucketKey(day, hour, model, effort, subagent);
        var record = new StatsRecord(provider, day, model, project, input, output, cacheCreation, cacheRead, hour, effort, Subagent: subagent);
        buckets[key] = buckets.TryGetValue(key, out var already) ? already.Add(record) : record;
    }

    /// <summary>What one file's lines are summed under: everything else of the usage key is the same
    /// for the whole file.</summary>
    private readonly record struct UsageBucketKey(DateOnly Day, int Hour, string Model, string Effort, bool Subagent);

    /// <summary>The real project path from the first transcript line that carries a "cwd" field,
    /// preferred over the sanitised folder name (<paramref name="fallback"/>) whenever such a line
    /// exists at all; a transcript with no such line keeps the folder name.</summary>
    private static string ProbeClaudeProject(string path, string fallback, CancellationToken cancellationToken)
    {
        foreach (var probeLine in ReadProbeLines(path, ProbeBytes, cancellationToken))
        {
            var fromLine = ClaudeUsageLogParser.TryExtractProjectFromLine(probeLine);
            if (fromLine is not null)
                return fromLine;
        }

        return fallback;
    }

    /// <summary>The remembered state from this walk's snapshot, else straight from the index (a file
    /// first seen during this walk). False when the index could not be read: the file is then skipped
    /// until the next walk rather than read again from the start.</summary>
    private bool TryFindState(string path, Dictionary<string, StatsSourceFileState> known, out StatsSourceFileState? state)
    {
        if (known.TryGetValue(path, out state))
            return true;
        return _store.TryGetSourceFile(path, out state);
    }

    private static FileInfo? TryStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
