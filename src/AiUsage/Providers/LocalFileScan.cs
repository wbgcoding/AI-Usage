namespace AiUsage.Providers;

/// <summary>
/// Shared, bounded file-tree scan for every provider that watches a local config or session folder
/// for its own quota file. The walk is lazy and depth-first: a folder's files come first (last write
/// time, newest first), then its subfolders, ranked by the newest of the folder's own write time and
/// the newest matching file directly inside it (appending to a file does not touch its folder's
/// stamp, so the folder time alone would bury a long-running session). Every entry a listing
/// returns counts against the touched-entry budget, the ranking listings included, and no deeper
/// level is walked once it is spent. Ranking has its own, larger budget: a level's children are
/// listed in folder write time order only while it lasts, the rest keep their folder time as rank.
/// It keeps only the newest <c>maxFiles</c> files and returns them newest first (by
/// <c>LastWriteTimeUtc</c>). A small
/// process-wide memory of the last result per root and pattern is checked first, so a file found
/// once is still found when the walk budget no longer reaches its folder.
/// </summary>
internal static class LocalFileScan
{
    internal static IReadOnlyList<FileInfo> NewestFiles(
        string root, string searchPattern, int maxDepth, int maxFiles, Func<string, bool>? isExcluded = null,
        int maxTouchedEntries = DefaultMaxTouchedEntries, int maxRankingEntries = DefaultMaxRankingEntries)
    {
        // A missing root is checked here, not inside the lazy walk, so it costs nothing.
        if (!Directory.Exists(root))
            return [];

        var remembered = Recall(root, searchPattern);
        var seen = new HashSet<string>(remembered, StringComparer.OrdinalIgnoreCase);
        var budget = new Budget(maxTouchedEntries);
        var ranking = new Budget(maxRankingEntries);
        var candidates = EnumerateRemembered(remembered)
            .Concat(WalkNewestFirst(ListDirectory(root, searchPattern, descendInto: maxDepth > 0, budget), searchPattern, maxDepth, depth: 0, budget, ranking)
                .Where(f => !seen.Contains(f.FullName)));

        var result = SelectNewest(candidates, maxFiles, isExcluded, maxTouchedEntries);
        Remember(root, searchPattern, result);
        return result;
    }

    /// <summary>A tree with more eligible entries than this must not turn "look at the newest files"
    /// into "walk everything that ever existed" - unrelated to <c>maxFiles</c>, which bounds the
    /// RESULT, not how much of the tree gets looked at. Past this many touched entries, the walk
    /// stops and returns whatever it has already selected. The newest-file ranking and the remembered
    /// last result bring the live file first, so a small budget is enough.</summary>
    internal const int DefaultMaxTouchedEntries = 5000;

    /// <summary>Entries one scan may list only to rank the children of a level. A tree with hundreds
    /// of project folders must not be listed in full on every tick just to order them.</summary>
    internal const int DefaultMaxRankingEntries = 20000;

    /// <summary>Entries listed so far across the whole walk.</summary>
    private sealed class Budget(int limit)
    {
        private int _listed;

        public bool Spent => _listed >= limit;

        public void Charge(int entries) => _listed += entries;
    }

    /// <summary>The selection over the candidate entries; each one is re-read from disk before it is
    /// compared, so what is kept (and later read by callers) is current.</summary>
    private static List<FileInfo> SelectNewest(IEnumerable<FileInfo> candidates, int maxFiles, Func<string, bool>? isExcluded, int maxTouchedEntries)
    {
        var kept = new List<FileInfo>(Math.Min(maxFiles, 64));

        try
        {
            var touched = 0;
            using var enumerator = candidates.GetEnumerator();
            while (touched < maxTouchedEntries && enumerator.MoveNext())
            {
                touched++;
                var file = enumerator.Current;

                if (isExcluded is not null && isExcluded(file.FullName))
                    continue;

                // The listing's own data is stale for a file a writer still holds open (the directory
                // entry is updated lazily), which is exactly the live session file this scan is for.
                file.Refresh();
                InsertKeepingNewest(kept, file, maxFiles);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: whatever was already collected before the failure is still worth returning.
        }

        kept.Reverse(); // kept is maintained oldest-first for O(1) eviction; the public contract is newest-first
        return kept;
    }

    /// <summary>Keeps <paramref name="kept"/> sorted oldest-first with at most
    /// <paramref name="maxFiles"/> entries: once full, a candidate no newer than the current oldest
    /// kept entry is dropped after a single comparison; a newer one is inserted in order and the
    /// old oldest entry is evicted.</summary>
    private static void InsertKeepingNewest(List<FileInfo> kept, FileInfo candidate, int maxFiles)
    {
        if (maxFiles <= 0)
            return;

        if (kept.Count == maxFiles && candidate.LastWriteTimeUtc <= kept[0].LastWriteTimeUtc)
            return;

        var insertAt = kept.Count;
        while (insertAt > 0 && kept[insertAt - 1].LastWriteTimeUtc > candidate.LastWriteTimeUtc)
            insertAt--;
        kept.Insert(insertAt, candidate);

        if (kept.Count > maxFiles)
            kept.RemoveAt(0);
    }

    private static readonly object MemoryLock = new();
    private static readonly Dictionary<string, string[]> Memory = [];

    /// <summary>Roots are few (one per provider and account); the cap only guards against a caller
    /// that scans an endless series of distinct folders.</summary>
    private const int MaxRememberedKeys = 64;

    private static string MemoryKey(string root, string searchPattern) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()
        + "|" + searchPattern.ToUpperInvariant();

    private static string[] Recall(string root, string searchPattern)
    {
        lock (MemoryLock)
            return Memory.TryGetValue(MemoryKey(root, searchPattern), out var paths) ? paths : [];
    }

    private static void Remember(string root, string searchPattern, List<FileInfo> result)
    {
        var paths = result.Select(f => f.FullName).ToArray();
        lock (MemoryLock)
        {
            if (Memory.Count >= MaxRememberedKeys)
                Memory.Clear();
            Memory[MemoryKey(root, searchPattern)] = paths;
        }
    }

    /// <summary>The files the last scan returned, as candidates ahead of the walk. A file that has
    /// since vanished is skipped; the selection re-reads the rest from disk.</summary>
    private static IEnumerable<FileInfo> EnumerateRemembered(string[] paths)
    {
        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (file.Exists)
                yield return file;
        }
    }

    private readonly record struct Listing(FileInfo[] Files, DirectoryInfo[] Subdirectories);

    /// <summary>Depth-first walk that yields the newest candidates first: a folder's files by write
    /// time descending, then its subfolders ranked by the newest of the folder's own stamp and its
    /// newest direct matching file, whatever its name. The children are listed for that rank in
    /// folder write time order while the ranking budget lasts, and a listing is handed down; the
    /// others keep their folder time as rank and are listed when the walk reaches them. The walk is
    /// lazy per level, so a consumer that stops after its entry budget never lists the older branches
    /// below the folder it stopped in; once the touched-entry budget is spent no deeper level is
    /// walked at all.</summary>
    private static IEnumerable<FileInfo> WalkNewestFirst(Listing listing, string searchPattern, int maxDepth, int depth, Budget budget, Budget ranking)
    {
        foreach (var file in listing.Files)
            yield return file;

        if (depth >= maxDepth || budget.Spent)
            yield break;

        var descend = depth + 1 < maxDepth;
        var ranked = listing.Subdirectories
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ThenByDescending(d => d.FullName, StringComparer.Ordinal)
            .Select(d =>
            {
                if (ranking.Spent)
                    return (Directory: d, Child: (Listing?)null, Rank: d.LastWriteTimeUtc);

                var child = ListDirectory(d.FullName, searchPattern, descend, budget);
                ranking.Charge(child.Files.Length + child.Subdirectories.Length);
                var rank = child.Files.Length > 0 && child.Files[0].LastWriteTimeUtc > d.LastWriteTimeUtc
                    ? child.Files[0].LastWriteTimeUtc
                    : d.LastWriteTimeUtc;
                return (Directory: d, Child: (Listing?)child, Rank: rank);
            })
            .ToArray()
            .OrderByDescending(r => r.Rank)
            .ThenByDescending(r => r.Directory.FullName, StringComparer.Ordinal);

        foreach (var entry in ranked)
        {
            var child = entry.Child ?? ListDirectory(entry.Directory.FullName, searchPattern, descend, budget);
            foreach (var file in WalkNewestFirst(child, searchPattern, maxDepth, depth + 1, budget, ranking))
                yield return file;
        }
    }

    private static Listing ListDirectory(string directory, string searchPattern, bool descendInto, Budget budget)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        try
        {
            var info = new DirectoryInfo(directory);
            var files = info.EnumerateFiles(searchPattern, options)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
            var subdirectories = descendInto ? info.EnumerateDirectories("*", options).ToArray() : [];
            budget.Charge(files.Length + subdirectories.Length);
            return new Listing(files, subdirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished or cannot be read is skipped; the rest of the tree still counts.
            return new Listing([], []);
        }
    }
}
