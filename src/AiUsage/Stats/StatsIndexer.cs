using System.Text;
using AiUsage.Io;
using AiUsage.Providers;
using AiUsage.Services;
using Microsoft.Data.Sqlite;

namespace AiUsage.Stats;

/// <summary>What one <see cref="StatsIndexer.IndexOnce"/> call actually did, per provider - the
/// number behind "how much of the real usage does this index actually cover". A line the walk chose
/// not to look at (a file skipped because its size and write time already matched what was stored)
/// counts toward neither <see cref="StatsIndexCounters.LinesParsed"/> nor <see
/// cref="StatsIndexCounters.LinesSkipped"/> - only lines an actually opened file yields land in either
/// bucket.</summary>
public readonly record struct StatsIndexResult(StatsIndexCounters Claude, StatsIndexCounters Codex, StatsIndexCounters Gemini = default);

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
/// Walks every Claude and Codex session log and every Antigravity conversation database on this
/// machine and folds whatever is new since the
/// last run into <see cref="StatsStore"/>. A single <see cref="IndexOnce"/> call is the whole
/// contract: safe to call repeatedly (a file whose size and write time match what is already stored
/// is skipped without being opened), safe to cancel mid-walk (already-finished files stay
/// finished - the next call simply continues with whatever is left), and never throws (a file that
/// vanishes or locks up mid-read is left for the next run rather than failing the whole walk).
///
/// Copilot is not walked here at all: it keeps no local token count to index - <see
/// cref="ProviderCoverage"/> is what the statistics window itself reads to say so.
/// </summary>
public sealed class StatsIndexer
{
    public const string ClaudeProviderId = "claude";
    public const string CodexProviderId = "codex";
    public const string GeminiProviderId = "gemini";

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

    /// <summary>The Antigravity conversation folders to read. Empty unless set: only the production
    /// constructor points it at this machine's real folders, so a test walk never reads them.</summary>
    internal IReadOnlyList<string> AntigravityRoots { get; init; } = [];

    public StatsIndexer(StatsStore store, LogService? logService = null)
        : this(store, DefaultClaudeProjectsRoots(), DefaultCodexSessionsRoot, DefaultCodexArchivedRoot, () => DateTime.UtcNow)
    {
        Log = logService;
        AntigravityRoots = Providers.Parsing.AntigravityTurnReader.DefaultRoots();
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

    // Files whose result could not be stored during the current walk; one log line at its end.
    private int _failedWrites;

    // Whether the walk consults and writes the owner of each Claude response (only once the one-time
    // owner pass has run).
    private bool _claimsActive;

    /// <summary>How many runs in a row a one-time pass waits for an unreadable file before it gives
    /// that file up and completes without it: a file locked for good must not stop indexing.</summary>
    internal const int MaxBlockedRuns = 5;

    // Per pass, this run: whether it still waits (decided once, so a pass that is called twice in
    // one run counts as one blocked run).
    private readonly Dictionary<string, bool> _passHeld = [];

    public StatsIndexResult IndexOnce(CancellationToken cancellationToken = default)
    {
        _failedWrites = 0;
        _passHeld.Clear();
        var claudeFilesSeen = 0;
        var claudeFilesRead = 0;
        var claudeLinesParsed = 0L;
        var claudeLinesSkipped = 0L;
        var claudeFoldersSkipped = 0;

        // One query for every remembered file instead of one database open per file: most walks find
        // thousands of files and change none of them.
        var known = _store.LoadSourceFiles();

        // Before the walk: the walk counts a response's fullest copy from the first line it reads, so
        // a pass that ran after it would add the same tokens to files the walk just read for the
        // first time. The passes it depends on come first so none of them undoes another.
        var passOpen = BackfillSessionsOnce(known, cancellationToken);
        passOpen |= BackfillSubagentUsageOnce(known, cancellationToken);
        var copiesOpen = BackfillClaudeCopiesOnce(known, cancellationToken);
        passOpen |= copiesOpen;

        // The passes above count every copy of a response; this one subtracts the copies a second
        // transcript counted, so it waits while the copies pass does (and counts that as its own
        // blocked run, so both give up together).
        if (!copiesOpen)
            passOpen |= BackfillClaudeOwnersOnce(known, cancellationToken);
        else if (!_store.IsClaudeOwnerPassDone())
            HoldPass("owners");

        // A pass that waits for an unreadable file reads the content up to the offsets stored now; a
        // walk in between would move them past content it counted differently and the pass would
        // count that again.
        if (passOpen)
        {
            Log?.LogInfo("Statistics: indexing waits for a session file that cannot be read yet.");
            return default;
        }

        // Until that pass has run, owners are neither read nor written, so the pass later handles
        // this content like any older content.
        _claimsActive = _store.IsClaudeOwnerPassConfirmedDone();

        var claudeFiles = new List<FileInfo>();
        foreach (var claudeRoot in _claudeProjectsRoots)
        {
            claudeFiles.AddRange(EnumerateNewestFirstWithInfo(claudeRoot, "*.jsonl", out var foldersSkipped, cancellationToken));
            claudeFoldersSkipped += foldersSkipped;
        }

        // A file new to the index is read after the files it may repeat: the first transcript to count
        // a response owns it, and a resumed session copies its parent's history. Once every file is
        // known only appended parts are read and the order no longer matters.
        if (claudeFiles.Any(file => !known.ContainsKey(file.FullName)))
        {
            claudeFiles.Sort((a, b) =>
            {
                var byAge = a.CreationTimeUtc.CompareTo(b.CreationTimeUtc);
                return byAge != 0 ? byAge : string.CompareOrdinal(a.FullName, b.FullName);
            });
        }

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

        var geminiFilesSeen = 0;
        var geminiFilesRead = 0;
        var geminiRowsParsed = 0L;
        var geminiRowsSkipped = 0L;
        var geminiFoldersSkipped = 0;
        // A conversation id counts once even when two folders hold a file of that name; a symlinked
        // database is not followed (the walk skips linked folders for the same reason).
        var conversationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var antigravityRoot in AntigravityRoots)
        {
            var conversations = EnumerateNewestFirstWithInfo(antigravityRoot, "*.db", out var skippedFolders, cancellationToken);
            geminiFoldersSkipped += skippedFolders;
            foreach (var listed in conversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (listed.Attributes.HasFlag(FileAttributes.ReparsePoint) || !conversationIds.Add(listed.Name))
                    continue;

                geminiFilesSeen++;
                var (read, rowsParsed, rowsSkipped) = IndexAntigravityFile(listed, known, cancellationToken);
                if (read)
                    geminiFilesRead++;
                geminiRowsParsed += rowsParsed;
                geminiRowsSkipped += rowsSkipped;
            }
        }

        // After the walk, so the usage that is not stored yet is never held up by a display-only pass,
        // and the pass sees the offsets the walk just reached. It runs from a snapshot read now; a
        // snapshot that could not be read skips the pass instead of finishing it with nothing.
        if (_store.TryLoadSourceFiles(out var afterWalk))
        {
            BackfillSessionsOnce(afterWalk, cancellationToken);
            BackfillSubagentUsageOnce(afterWalk, cancellationToken);
        }

        if (_failedWrites > 0)
            Log?.LogError($"Statistics: the results of {_failedWrites} session file(s) could not be stored (the index is locked or unwritable); they are read again on the next walk.");

        return new StatsIndexResult(
            new StatsIndexCounters(claudeFilesSeen, claudeFilesRead, claudeLinesParsed, claudeLinesSkipped, claudeFoldersSkipped),
            new StatsIndexCounters(codexFilesSeen, codexFilesRead, codexLinesParsed, codexLinesSkipped, codexFoldersSkipped),
            new StatsIndexCounters(geminiFilesSeen, geminiFilesRead, geminiRowsParsed, geminiRowsSkipped, geminiFoldersSkipped));
    }

    /// <summary>Every matching file under <paramref name="root"/>, newest write time first - so a
    /// window opened while the very first walk is still running already shows the newest days
    /// instead of whatever the filesystem happened to enumerate first. One unreadable
    /// subdirectory never hides every other file under <paramref name="root"/>: each subdirectory is
    /// listed on its own, and one that throws is counted into <paramref name="foldersSkipped"/>
    /// rather than failing the whole walk or vanishing without a trace the way blanket <see
    /// cref="EnumerationOptions.IgnoreInaccessible"/> does.</summary>
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

    /// <summary>Counts a write that did not land, so the walk can say so once at its end.</summary>
    private bool NoteStored(bool stored)
    {
        if (!stored)
            _failedWrites++;
        return stored;
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
            // The owner pass, when still to run, must not read that unread part either.
            KeepShrunkClaudeFile(existing, info.Length, info.LastWriteTimeUtc);
            return (false, 0, 0);
        }

        // A copy of a response that another transcript counted first (a resumed or forked session
        // repeats the history of its parent) adds nothing here. Without the lookup the file is left
        // for the next walk rather than counted blind.
        using var owners = _claimsActive ? _store.OpenClaudeOwners() : null;
        if (_claimsActive && owners is null)
            return (false, 0, 0);

        var offset = existing?.Offset ?? 0;
        var ownFileId = owners?.FileIdOf(existing?.Path ?? path);
        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        var copies = new ClaudeCopyTracker(existing);
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
            if (IsShorterThanRead(existing, openLength))
            {
                // Replaced or cut between the stat above and this open: handled like a shrunk file
                // rather than read again from the start (the reader falls back to 0 past the end).
                // The owner read ends first: an open read would block the write.
                owners?.EndRead();
                KeepShrunkClaudeFile(existing, openLength, info.LastWriteTimeUtc);
                return (false, 0, 0);
            }

            consumedOffset = CompleteLineReader.Read(stream, offset, acceptTrailingLineWithoutNewline, MaxRecordBytes, LogOversizedRecord, line =>
            {
                if (ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                {
                    if (owners is not null && IsCountedByAnotherFile(usageEvent.MessageKey, ownFileId, copies, owners, foreignKeys))
                    {
                        linesSkipped++;
                        return;
                    }

                    // The same response is written more than once while it streams, the early copies with
                    // placeholder output counts: the first copy counts, a fuller one adds its excess.
                    if (copies.Observe(usageEvent) is not { } add)
                    {
                        linesSkipped++;
                        return;
                    }

                    linesParsed++;
                    var subagent = fileIsSubagent || usageEvent.IsSidechain;
                    Accumulate(buckets, ClaudeProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                        add.Input, add.Output, add.CacheCreation, add.CacheRead, usageEvent.Effort, subagent);
                    sessions.Add(ClaudeProviderId, usageEvent.SessionId.Length > 0 ? usageEvent.SessionId : fallbackSessionId, project,
                        usageEvent.Timestamp, usageEvent.Model, subagent,
                        add.Input, add.Output, add.CacheCreation, add.CacheRead);
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

        // The read ends before the write below: an open read would block its commit.
        owners?.EndRead();

        // A lookup that failed may have let a foreign copy through: nothing is stored from this read.
        if (owners is { Failed: true })
            return (false, 0, 0);

        var lastCounted = copies.CountedForLastKey;
        var stored = _store.ApplyIndexResult(
            [.. buckets.Values],
            new StatsSourceFileState(
                existing?.Path ?? path, ClaudeProviderId, consumedOffset, openLength, info.LastWriteTimeUtc,
                LastMessageKey: copies.LastKey,
                CountedInputTokens: lastCounted?.Input, CountedOutputTokens: lastCounted?.Output,
                CountedCacheCreationTokens: lastCounted?.CacheCreation, CountedCacheReadTokens: lastCounted?.CacheRead),
            sessions.ToDeltas(), _claimsActive ? copies.Keys : null);
        return NoteStored(stored) ? (true, linesParsed, linesSkipped) : (false, 0, 0);
    }

    /// <summary>True when <paramref name="key"/> is a response another transcript counted first. A key
    /// this file has already counted (or settled as foreign) is not looked up again; an empty key
    /// belongs to no one.</summary>
    private static bool IsCountedByAnotherFile(
        string key, long? ownFileId, ClaudeCopyTracker copies, StatsStore.ClaudeOwnerLookup owners, HashSet<string> foreignKeys)
    {
        if (key.Length == 0 || copies.HasCounted(key))
            return false;
        if (foreignKeys.Contains(key))
            return true;

        var owner = owners.OwnerOf(key);
        if (owner is null || owner == ownFileId)
            return false;

        foreignKeys.Add(key);
        return true;
    }

    /// <summary>What a Claude file's repeated responses have added to the totals so far. A response
    /// is written several times while it streams and the early copies carry placeholder output
    /// counts, so the first copy of a key counts as it stands and every later copy adds only the part
    /// by which it exceeds what is counted (per field, never negative). Only the last response is
    /// kept across runs, with its counted tokens; a row from before those were stored seeds from the
    /// first copy met, which is then not counted again.</summary>
    private sealed class ClaudeCopyTracker
    {
        private readonly Dictionary<string, ClaudeCopyTokens> _counted = new(StringComparer.Ordinal);
        private string? _legacyKey;

        public ClaudeCopyTracker(StatsSourceFileState? existing)
        {
            LastKey = existing?.LastMessageKey ?? "";
            if (LastKey.Length == 0)
                return;

            if (existing!.CountedForLastMessage is { } counted)
                _counted[LastKey] = counted;
            else
                _legacyKey = LastKey;
        }

        /// <summary>The key of the latest response seen, kept for the next run.</summary>
        public string LastKey { get; private set; }

        /// <summary>The tokens counted for <see cref="LastKey"/>; null when it is not known.</summary>
        public ClaudeCopyTokens? CountedForLastKey =>
            LastKey.Length > 0 && _counted.TryGetValue(LastKey, out var counted) ? counted : null;

        public ClaudeCopyTokens? CountedFor(string key) => _counted.TryGetValue(key, out var counted) ? counted : null;

        public bool HasCounted(string key) => _counted.ContainsKey(key);

        /// <summary>Every response this file counted a copy of, to be recorded as its own.</summary>
        public IReadOnlyCollection<string> Keys => _counted.Keys;

        /// <summary>The tokens this copy adds to the totals; null when it adds nothing.</summary>
        public ClaudeCopyTokens? Observe(ClaudeUsageLogParser.UsageEvent usageEvent) => Observe(usageEvent, out _);

        /// <param name="isFirstCopy">True when the returned tokens are the whole first copy.</param>
        public ClaudeCopyTokens? Observe(ClaudeUsageLogParser.UsageEvent usageEvent, out bool isFirstCopy)
        {
            isFirstCopy = false;
            var copy = new ClaudeCopyTokens(
                usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens);
            var key = usageEvent.MessageKey;
            if (key.Length == 0)
            {
                isFirstCopy = true;
                return copy;
            }

            LastKey = key;
            if (!_counted.TryGetValue(key, out var counted))
            {
                _counted[key] = copy;
                if (key == _legacyKey)
                {
                    _legacyKey = null;
                    return null;
                }

                isFirstCopy = true;
                return copy;
            }

            var excess = copy.ExcessOver(counted);
            if (excess.IsZero)
                return null;

            _counted[key] = counted.MaxWith(copy);
            return excess;
        }
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
            NoteStored(_store.SetSourceFile(existing with { Offset = info.Length, Size = info.Length, WriteTimeUtc = info.LastWriteTimeUtc }));
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
            if (ReadFirstLineCapped(path, CodexFirstLineBytes) is { } firstLine)
            {
                project = CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(firstLine) ?? "";
                sessionId = CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(firstLine) ?? sessionId;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            openLength = stream.Length;
            if (IsShorterThanRead(existing, openLength))
            {
                // Replaced or cut between the stat above and this open: handled like a shrunk file
                // rather than read again from the start (the reader falls back to 0 past the end).
                NoteStored(_store.SetSourceFile(existing with { Offset = openLength, Size = openLength, WriteTimeUtc = info.LastWriteTimeUtc }));
                return (false, 0, 0);
            }

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
        var stored = _store.ApplyIndexResult(
            [.. buckets.Values],
            new StatsSourceFileState(
                existing?.Path ?? path, CodexProviderId, consumedOffset, openLength, info.LastWriteTimeUtc,
                state.Model, state.CumulativeInput, state.CumulativeOutput, state.CumulativeCacheCreation, state.CumulativeCacheRead,
                state.Effort),
            sessions.ToDeltas());
        return NoteStored(stored) ? (true, linesParsed, linesSkipped) : (false, 0, 0);
    }

    /// <summary>Reads the model calls added to one Antigravity conversation file since the last run.
    /// The file counts as unchanged while the size and write time of the database plus its write-ahead
    /// log match what was stored (the tool keeps new rows in the log for a while). The stored offset is
    /// the next row index to read, so a row is counted once however often the file is looked at.
    /// Anything that stops the read (the tool holds a lock, the file is not a database) leaves the
    /// stored state as it was, so the file is tried again on the next walk.</summary>
    private (bool Read, long RowsParsed, long RowsSkipped) IndexAntigravityFile(
        FileInfo listed, Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        var path = listed.FullName;
        if (TryStat(path) is not { } info)
            return (false, 0, 0);

        var size = info.Length;
        var writeTime = info.LastWriteTimeUtc;
        // An empty log is what a reader leaves behind just by opening the file; only a log with content counts.
        if (TryStat(path + "-wal") is { Length: > 0 } log)
        {
            size += log.Length;
            if (log.LastWriteTimeUtc > writeTime)
                writeTime = log.LastWriteTimeUtc;
        }

        if (!TryFindState(path, known, out var existing))
            return (false, 0, 0);
        if (existing is not null && existing.Size == size && existing.WriteTimeUtc == writeTime)
            return (false, 0, 0);

        AntigravityUsageLogParser.ReadResult result;
        try
        {
            result = AntigravityUsageLogParser.ReadConversation(path, existing?.Offset ?? 0, writeTime, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            || ex is SqliteException { SqliteErrorCode: not (1 or 11 or 26) })
        {
            // Locked by the tool, or briefly unreadable: tried again on the next walk.
            return (false, 0, 0);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // A file that is not a usable database (wrong schema, corrupt, unexpected value types).
            // It is remembered as it is now, so it is skipped, and logged once, until it changes.
            Log?.LogError($"Statistics: an Antigravity conversation file could not be read and is skipped until it changes ({ex.GetType().Name}).");
            NoteStored(_store.ApplyIndexResult([], new StatsSourceFileState(existing?.Path ?? path, GeminiProviderId, existing?.Offset ?? 0, size, writeTime), []));
            return (false, 0, 0);
        }

        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        var sessions = new StatsSessionAccumulator();
        var sessionId = Path.GetFileNameWithoutExtension(path);
        foreach (var call in result.Events)
        {
            Accumulate(buckets, GeminiProviderId, result.Project, call.Timestamp, call.Model,
                call.InputTokens, call.OutputTokens, 0, call.CacheReadTokens, "", subagent: false);
            sessions.Add(GeminiProviderId, sessionId, result.Project, call.Timestamp, call.Model, subagent: false,
                call.InputTokens, call.OutputTokens, 0, call.CacheReadTokens);
        }

        var stored = _store.ApplyIndexResult(
            [.. buckets.Values],
            // A read that stopped early is stored with a size that never matches, so the next walk
            // continues it even when the file itself does not change.
            new StatsSourceFileState(existing?.Path ?? path, GeminiProviderId, result.NextIndex, result.Incomplete ? -1 : size, writeTime),
            sessions.ToDeltas());
        return NoteStored(stored) ? (true, result.Events.Count, result.RowsSkipped) : (false, 0, 0);
    }

    /// <summary>The one-time pass after a migration that left usage rows without sessions: every
    /// remembered file that still exists is read again, up to the offset the usage rows already
    /// cover, into session totals only. The usage rows and the file markers are not touched, so the
    /// sessions add up to the usage they belong to, and a file that grew meanwhile contributes its new
    /// part through the normal walk that follows. A file that is gone or shorter than its marker (it
    /// was replaced) stays out. Nothing is marked done when the pass is cancelled or cannot be
    /// written, so the next walk repeats it.</summary>
    private bool BackfillSessionsOnce(Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        if (_store.IsSessionBackfillDone())
            return false;

        // No remembered file while usage rows exist: the snapshot is not trustworthy, so the pass is
        // not run and not marked done (it would never be offered again).
        if (known.Count == 0 && _store.HasUsageRows())
            return false;

        // Once the owner pass has run, a response another transcript owns is not counted here either
        // (this pass can follow a walk that already skipped those copies). Before it, the content
        // counts like every older content and the owner pass takes the copies out later.
        var claims = _store.IsClaudeOwnerPassConfirmedDone();
        using var owners = claims ? _store.OpenClaudeOwners() : null;
        if (owners is null && claims)
            return false;

        var sessions = new StatsSessionAccumulator();
        var unreadable = false;
        foreach (var state in known.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Offset <= 0)
                continue;

            try
            {
                if (state.Provider == ClaudeProviderId)
                    BackfillClaudeFile(state, owners, sessions, cancellationToken);
                else if (state.Provider == CodexProviderId)
                    BackfillCodexFile(state, sessions, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished has no session data to give; one that is locked is read again later.
                unreadable |= !IsGone(ex);
            }
            finally
            {
                owners?.EndRead();
            }
        }

        if (owners is { Failed: true } || (unreadable && HoldPass("sessions")))
            return unreadable;

        _store.ReplaceSessionsAndMarkBackfilled(sessions.ToDeltas());
        return false;
    }

    /// <summary>The one-time pass for usage rows written before the subagent flag existed: they all
    /// hold the flag as off. Every remembered Claude file is read again up to its stored offset, the
    /// subagent lines are summed per usage key, and the store moves exactly those tokens over to the
    /// flagged rows (never more than the unflagged row holds). Nothing is marked done when the pass is
    /// cancelled or cannot be written, so the next walk repeats it.</summary>
    private bool BackfillSubagentUsageOnce(Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        if (_store.IsSubagentUsageBackfillDone())
            return false;

        if (known.Count == 0 && _store.HasUsageRows())
            return false;

        var totals = new Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort), StatsRecord>();
        var unreadable = false;
        foreach (var state in known.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Provider != ClaudeProviderId || state.Offset <= 0)
                continue;

            try
            {
                SumClaudeSubagentTokens(state, totals, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished has nothing to give; one that is locked is read again later.
                unreadable |= !IsGone(ex);
            }
        }

        if (unreadable && HoldPass("subagents"))
            return true;

        if (_store.MoveSubagentUsageAndMarkBackfilled([.. totals.Values]) is { } moved)
            Log?.LogInfo($"Statistics: {moved} tokens of earlier usage were assigned to subagents.");
        return false;
    }

    private void SumClaudeSubagentTokens(
        StatsSourceFileState state, Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort), StatsRecord> totals,
        CancellationToken cancellationToken)
    {
        var path = state.Path;
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var countedKeys = new HashSet<string>(StringComparer.Ordinal);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        var project = ProbeClaudeProject(path, ClaudeUsageLogParser.ExtractProjectFromFilePath(path), cancellationToken);
        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        CompleteLineReader.Read(stream, 0, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (!ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                return;
            if (usageEvent.MessageKey.Length > 0 && !countedKeys.Add(usageEvent.MessageKey))
                return;
            if (!fileIsSubagent && !usageEvent.IsSidechain)
                return;

            Accumulate(buckets, ClaudeProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                usageEvent.InputTokens, usageEvent.OutputTokens, usageEvent.CacheCreationTokens, usageEvent.CacheReadTokens,
                usageEvent.Effort, subagent: true);
        }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), endLimit: state.Offset);

        foreach (var record in buckets.Values)
        {
            var key = (record.Day, record.Hour, record.Model, record.Project, record.Effort);
            totals[key] = totals.TryGetValue(key, out var already) ? already.Add(record) : record;
        }
    }

    /// <summary>The one-time pass for Claude responses that earlier builds counted by their first copy
    /// only. Every remembered Claude file is read again up to its stored offset and the part by which
    /// the later copies of each response exceed the first is added to the usage rows and the session
    /// totals, attributed exactly as the walk attributes it now. The counted tokens of each file's
    /// last response are stored too, so the walk carries on from them. A file Claude Code has already
    /// deleted cannot be corrected. Nothing is marked done when the pass is cancelled or cannot be
    /// written, so the next walk repeats it.</summary>
    private bool BackfillClaudeCopiesOnce(Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        if (_store.IsClaudeCopyBackfillDone())
            return false;

        if (known.Count == 0 && _store.HasUsageRows())
            return false;

        var totals = new Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort, bool Subagent), StatsRecord>();
        var sessions = new StatsSessionAccumulator();
        var lastMessages = new List<(string Path, string MessageKey, ClaudeCopyTokens Counted)>();
        var unreadable = false;
        foreach (var state in known.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Provider != ClaudeProviderId || state.Offset <= 0)
                continue;

            try
            {
                SumClaudeCopyExcess(state, totals, sessions, lastMessages, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished has nothing to give; one that is locked is read again later.
                unreadable |= !IsGone(ex);
            }
        }

        if (unreadable && HoldPass("copies"))
            return true;

        if (_store.ApplyClaudeCopyBackfill([.. totals.Values], sessions.ToDeltas(), lastMessages) is { } added)
            Log?.LogInfo($"Statistics: {added} output tokens of earlier Claude responses were added.");
        return false;
    }

    private void SumClaudeCopyExcess(
        StatsSourceFileState state,
        Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort, bool Subagent), StatsRecord> totals,
        StatsSessionAccumulator sessions,
        List<(string Path, string MessageKey, ClaudeCopyTokens Counted)> lastMessages,
        CancellationToken cancellationToken)
    {
        var path = state.Path;
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var fallbackSessionId = ClaudeUsageLogParser.FallbackSessionId(path);
        var copies = new ClaudeCopyTracker(null);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        var project = ProbeClaudeProject(path, ClaudeUsageLogParser.ExtractProjectFromFilePath(path), cancellationToken);
        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        CompleteLineReader.Read(stream, 0, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (!ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                return;

            // The first copy is what the old walk counted; only what later copies add is missing.
            if (copies.Observe(usageEvent, out var isFirstCopy) is not { } add || isFirstCopy)
                return;

            var subagent = fileIsSubagent || usageEvent.IsSidechain;
            Accumulate(buckets, ClaudeProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                add.Input, add.Output, add.CacheCreation, add.CacheRead, usageEvent.Effort, subagent);
            sessions.Add(ClaudeProviderId, usageEvent.SessionId.Length > 0 ? usageEvent.SessionId : fallbackSessionId, project,
                usageEvent.Timestamp, usageEvent.Model, subagent, add.Input, add.Output, add.CacheCreation, add.CacheRead);
        }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), endLimit: state.Offset);

        foreach (var record in buckets.Values)
        {
            var key = (record.Day, record.Hour, record.Model, record.Project, record.Effort, record.Subagent);
            totals[key] = totals.TryGetValue(key, out var already) ? already.Add(record) : record;
        }

        if (state.LastMessageKey.Length > 0 && copies.CountedFor(state.LastMessageKey) is { } counted)
            lastMessages.Add((path, state.LastMessageKey, counted));
    }

    /// <summary>The one-time pass for Claude responses that two transcripts both counted (a resumed or
    /// forked session repeats the history of its parent). The Claude files that still exist are read up
    /// to their stored offsets, oldest first, and the first one to hold a response owns it. Whatever any
    /// other file counted of that response (its first copy plus what later copies added) is then taken
    /// out of the usage rows and session totals it was added to. A file Claude Code has already
    /// deleted cannot be corrected and stays as it is. Every file is read before any owner is written,
    /// and the owners replace whatever an earlier pass left, in one transaction: a file that could not
    /// be read leaves nothing behind. The subtraction follows in one transaction with the done mark,
    /// so a cancelled or failed pass is simply repeated.</summary>
    private bool BackfillClaudeOwnersOnce(Dictionary<string, StatsSourceFileState> known, CancellationToken cancellationToken)
    {
        if (_store.IsClaudeOwnerPassDone())
            return false;

        // An empty snapshot may be a failed read: ask again, and leave the pass for the next walk
        // when that fails too (HasUsageRows answers false on an error, so it cannot tell). With no
        // Claude file remembered there is nothing to correct and the pass is marked done below.
        if (known.Count == 0 && !_store.TryLoadSourceFiles(out known))
            return false;

        if (_store.LoadClaudeOwnerSkips() is not { } skips)
            return false;

        // The original transcript is the oldest file: a copy of its history is made later. A file
        // whose offset moved without a read is read from where its counted content starts. Known
        // limit: a shrunk file reset by a build before the skip rows left no trace, so its never
        // counted prefix is read like counted content and copies of it may be taken out wrongly.
        var files = new List<(StatsSourceFileState State, DateTime Created, long Start)>();
        foreach (var state in known.Values)
        {
            if (state.Provider != ClaudeProviderId || state.Offset <= 0)
                continue;

            var start = skips.GetValueOrDefault(state.Path);
            var info = TryStat(state.Path);
            if (info is not null && start < state.Offset)
                files.Add((state, info.CreationTimeUtc, start));
        }

        files.Sort((a, b) =>
        {
            var byAge = a.Created.CompareTo(b.Created);
            return byAge != 0 ? byAge : string.CompareOrdinal(a.State.Path, b.State.Path);
        });

        var unreadable = false;
        var claims = new List<(string Path, IReadOnlyCollection<long> Hashes)>();
        foreach (var (state, _, start) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var keys = ReadClaudeKeys(state, start, cancellationToken);
                if (keys.Count > 0)
                    claims.Add((state.Path, [.. keys.Select(StatsStore.ClaudeKeyHash)]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished has nothing to give; one that is locked is read again later.
                unreadable |= !IsGone(ex);
            }
        }

        // The correction is final: with a readable-later file missing from it, it would stay wrong.
        if (unreadable && HoldPass("owners"))
            return true;

        if (!_store.ReplaceClaudeClaims(claims))
            return false;

        var totals = new Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort, bool Subagent), StatsRecord>();
        var sessions = new StatsSessionAccumulator();
        using (var owners = _store.OpenClaudeOwners())
        {
            if (owners is null)
                return false;

            foreach (var (state, _, start) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SumClaudeForeignCopies(state, start, owners, totals, sessions, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished has nothing to give; one that is locked is read again later.
                    unreadable |= !IsGone(ex);
                }
                finally
                {
                    owners.EndRead();
                }
            }

            if (owners.Failed)
                return false;
        }

        if (unreadable && HoldPass("owners"))
            return true;

        if (_store.ApplyClaudeOwnerCorrection([.. totals.Values], sessions.ToDeltas()) is not { } correction)
            return false;

        if (correction.Removed > 0)
            Log?.LogInfo($"Statistics: {correction.Removed} tokens of Claude responses counted twice were taken out.");
        if (correction.Skipped > 0)
            Log?.LogInfo($"Statistics: {correction.Skipped} corrections were skipped because their hour no longer holds that many tokens.");
        return false;
    }

    /// <summary>True while the one-time pass <paramref name="pass"/> should wait for a file it could
    /// not read; false from the <see cref="MaxBlockedRuns"/>th blocked run in a row on, when the file
    /// is given up. A count that cannot be stored keeps waiting. Decided once per run.</summary>
    private bool HoldPass(string pass)
    {
        if (_passHeld.TryGetValue(pass, out var held))
            return held;

        var runs = _store.NoteBlockedRun(pass);
        held = runs is not { } count || count < MaxBlockedRuns;
        if (!held)
            Log?.LogInfo($"Statistics: a session file stayed unreadable for {MaxBlockedRuns} runs; the one-time correction goes on without it.");
        _passHeld[pass] = held;
        return held;
    }

    private static bool IsGone(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;

    private HashSet<string> ReadClaudeKeys(StatsSourceFileState state, long start, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return keys;

        CompleteLineReader.Read(stream, start, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (ClaudeUsageLogParser.TryParse(line, out var usageEvent) && usageEvent.MessageKey.Length > 0)
                keys.Add(usageEvent.MessageKey);
        }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), endLimit: state.Offset);
        return keys;
    }

    private void SumClaudeForeignCopies(
        StatsSourceFileState state, long start, StatsStore.ClaudeOwnerLookup owners,
        Dictionary<(DateOnly Day, int Hour, string Model, string Project, string Effort, bool Subagent), StatsRecord> totals,
        StatsSessionAccumulator sessions, CancellationToken cancellationToken)
    {
        var path = state.Path;
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var fallbackSessionId = ClaudeUsageLogParser.FallbackSessionId(path);
        var copies = new ClaudeCopyTracker(null);
        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        var ownKeys = new HashSet<string>(StringComparer.Ordinal);
        var ownFileId = owners.FileIdOf(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        var project = ProbeClaudeProject(path, ClaudeUsageLogParser.ExtractProjectFromFilePath(path), cancellationToken);
        var buckets = new Dictionary<UsageBucketKey, StatsRecord>();
        CompleteLineReader.Read(stream, start, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (!ClaudeUsageLogParser.TryParse(line, out var usageEvent) || usageEvent.MessageKey.Length == 0)
                return;

            // Only the responses another file owns: the tracker then yields exactly what this file
            // added for them (the first copy, then what later copies exceeded it by).
            var key = usageEvent.MessageKey;
            if (!foreignKeys.Contains(key))
            {
                if (ownKeys.Contains(key))
                    return;

                var owner = owners.OwnerOf(key);
                if (owner is null || owner == ownFileId)
                {
                    ownKeys.Add(key);
                    return;
                }

                foreignKeys.Add(key);
            }

            if (copies.Observe(usageEvent) is not { } add)
                return;

            var subagent = fileIsSubagent || usageEvent.IsSidechain;
            Accumulate(buckets, ClaudeProviderId, project, usageEvent.Timestamp, usageEvent.Model,
                add.Input, add.Output, add.CacheCreation, add.CacheRead, usageEvent.Effort, subagent);
            sessions.Add(ClaudeProviderId, usageEvent.SessionId.Length > 0 ? usageEvent.SessionId : fallbackSessionId, project,
                usageEvent.Timestamp, usageEvent.Model, subagent, add.Input, add.Output, add.CacheCreation, add.CacheRead);
        }, cancellationToken, line => ClaudeUsageLogParser.MayContainUsage(line), endLimit: state.Offset);

        foreach (var record in buckets.Values)
        {
            var key = (record.Day, record.Hour, record.Model, record.Project, record.Effort, record.Subagent);
            totals[key] = totals.TryGetValue(key, out var already) ? already.Add(record) : record;
        }
    }

    private void BackfillClaudeFile(
        StatsSourceFileState state, StatsStore.ClaudeOwnerLookup? owners, StatsSessionAccumulator sessions, CancellationToken cancellationToken)
    {
        var path = state.Path;
        var project = ProbeClaudeProject(path, ClaudeUsageLogParser.ExtractProjectFromFilePath(path), cancellationToken);
        var fileIsSubagent = ClaudeUsageLogParser.IsSubagentFile(path);
        var fallbackSessionId = ClaudeUsageLogParser.FallbackSessionId(path);
        var countedKeys = new HashSet<string>(StringComparer.Ordinal);
        var ownFileId = owners?.FileIdOf(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            return;

        CompleteLineReader.Read(stream, 0, true, MaxRecordBytes, LogOversizedRecord, line =>
        {
            if (!ClaudeUsageLogParser.TryParse(line, out var usageEvent))
                return;
            if (usageEvent.MessageKey.Length > 0 && !countedKeys.Add(usageEvent.MessageKey))
                return;
            if (owners is not null && usageEvent.MessageKey.Length > 0
                && owners.OwnerOf(usageEvent.MessageKey) is { } owner && owner != ownFileId)
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
        if (ReadFirstLineCapped(path, CodexFirstLineBytes) is { } firstLine)
        {
            project = CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(firstLine) ?? "";
            sessionId = CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(firstLine) ?? sessionId;
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

    /// <summary>The longest first line of a Codex file that is still read (its session meta line holds
    /// the project and the session id). A longer one, or a file with no line break at all, is treated
    /// as having none instead of being read into memory whole.</summary>
    internal const int CodexFirstLineBytes = 1024 * 1024;

    /// <summary>The first line of the file, or null when it is empty or the line is longer than
    /// <paramref name="maxBytes"/>. Never holds more than the cap in memory.</summary>
    internal static string? ReadFirstLineCapped(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        using var collected = new MemoryStream();
        while (collected.Length < maxBytes)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - collected.Length));
            if (read <= 0)
                break;

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                collected.Write(buffer, 0, newline);
                return AppEncoding.Utf8NoBom.GetString(collected.GetBuffer(), 0, (int)collected.Length).TrimEnd('\r');
            }

            collected.Write(buffer, 0, read);
        }

        // No line break within the cap: a short file that simply ends is one line, a long one is not read.
        return stream.Position >= stream.Length && collected.Length > 0 && collected.Length < maxBytes
            ? AppEncoding.Utf8NoBom.GetString(collected.GetBuffer(), 0, (int)collected.Length).TrimEnd('\r')
            : null;
    }

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
            yield return AppEncoding.Utf8NoBom.GetString(buffer, start, lineEnd - start).TrimEnd('\r');
            start = lineEnd + 1;
        }
    }

    private void LogOversizedRecord() =>
        Log?.LogError("Statistics: a session record larger than the size limit was skipped.");

    /// <summary>The day and hour bucket are this machine's own LOCAL day/hour of <paramref
    /// name="timestamp"/>, not UTC - a session made at 23:30 local time in a zone ahead of UTC would
    /// land on the next UTC day, and every hour in the "by hour" chart would read one or two hours off
    /// in Germany (UTC+1/+2). <see cref="MainViewModel.RefreshWindowTokens"/> already queries from
    /// each window start in local time, so this makes the index consistent with it rather than the other
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

    /// <summary>A Claude file that shrank is left unread: only its remembered size and write time move,
    /// and the owner pass, when still to run, is told to skip the part never read.</summary>
    private void KeepShrunkClaudeFile(StatsSourceFileState existing, long length, DateTime writeTimeUtc)
    {
        if (!_claimsActive && !_store.SetClaudeOwnerSkip(existing.Path, length))
        {
            NoteStored(false);
            return;
        }

        NoteStored(_store.SetSourceFile(existing with { Offset = length, Size = length, WriteTimeUtc = writeTimeUtc }));
    }

    /// <summary>True when the opened file is shorter than what was already read of it - checked on the
    /// open stream, since the file can change after it was listed.</summary>
    internal static bool IsShorterThanRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] StatsSourceFileState? existing, long openLength) =>
        existing is not null && openLength < existing.Offset;

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
