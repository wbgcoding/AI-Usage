namespace AiUsage.Providers;

/// <summary>
/// Shared, bounded file-tree scan for every provider that watches a local config or session folder
/// for its own quota file. The walk is lazy and depth-first: a folder's files come first (last write
/// time, newest first), then its subfolders, most recently changed first. It touches at most
/// <see cref="MaxTouchedEntries"/> entries however large the tree is, keeps only the newest
/// <c>maxFiles</c> files and returns them newest first (by <c>LastWriteTimeUtc</c>).
/// </summary>
internal static class LocalFileScan
{
    internal static IReadOnlyList<FileInfo> NewestFiles(
        string root, string searchPattern, int maxDepth, int maxFiles, Func<string, bool>? isExcluded = null) =>
        SelectNewest(EnumerateCandidates(root, searchPattern, maxDepth), maxFiles, isExcluded);

    /// <summary>A tree with more eligible entries than this must not turn "look at the newest files"
    /// into "walk everything that ever existed" - unrelated to <c>maxFiles</c>, which bounds the
    /// RESULT, not how much of the tree gets looked at. Past this many touched entries, the walk
    /// stops and returns whatever it has already selected.</summary>
    private const int MaxTouchedEntries = 5000;

    /// <summary>
    /// The actual bounding + selection logic, over any path sequence - split out from the file-system
    /// walk above purely so a test can hand it a counting wrapper around the real enumeration and
    /// prove the ceiling is respected, without needing to fake the file system itself. Every eligible
    /// entry up to <see cref="MaxTouchedEntries"/> is looked at (one <see cref="FileInfo"/> stat each,
    /// not a file open), but only the newest <paramref name="maxFiles"/> of them are ever held onto -
    /// a bounded insert into a list kept sorted oldest-first, so memory stays proportional to
    /// <paramref name="maxFiles"/> the whole walk through. <paramref name="isExcluded"/> is checked
    /// per touched path and, when true, drops that file from the result without ever being compared
    /// against the kept set - it still counts toward <see cref="MaxTouchedEntries"/>, since skipping
    /// it is still a touch.
    /// </summary>
    internal static IReadOnlyList<FileInfo> NewestFiles(IEnumerable<string> candidatePaths, int maxFiles, Func<string, bool>? isExcluded = null) =>
        SelectNewest(candidatePaths.Select(path => new FileInfo(path)), maxFiles, isExcluded);

    /// <summary>The selection over the candidate entries; each one is re-read from disk before it is
    /// compared, so what is kept (and later read by callers) is current.</summary>
    private static List<FileInfo> SelectNewest(IEnumerable<FileInfo> candidates, int maxFiles, Func<string, bool>? isExcluded)
    {
        var kept = new List<FileInfo>(Math.Min(maxFiles, 64));

        try
        {
            var touched = 0;
            using var enumerator = candidates.GetEnumerator();
            while (touched < MaxTouchedEntries && enumerator.MoveNext())
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

    private static IEnumerable<FileInfo> EnumerateCandidates(string root, string searchPattern, int maxDepth)
    {
        // A missing root is checked here, not inside the lazy walk, so it costs nothing.
        if (!Directory.Exists(root))
            return [];

        return WalkNewestFirst(root, searchPattern, maxDepth, depth: 0);
    }

    /// <summary>Depth-first walk that yields the newest candidates first: subdirectories by last
    /// write time descending (a folder that just received a file is the one worth reaching before the
    /// budget runs out, whatever its name), files of each folder by write time descending. The walk
    /// is lazy, so a consumer that stops after its entry budget never lists the older folders at
    /// all.</summary>
    private static IEnumerable<FileInfo> WalkNewestFirst(string directory, string searchPattern, int maxDepth, int depth)
    {
        var (files, subdirectories) = ListDirectory(directory, searchPattern, descendInto: depth < maxDepth);

        foreach (var file in files)
            yield return file;

        foreach (var subdirectory in subdirectories)
            foreach (var file in WalkNewestFirst(subdirectory, searchPattern, maxDepth, depth + 1))
                yield return file;
    }

    private static (FileInfo[] Files, string[] Subdirectories) ListDirectory(string directory, string searchPattern, bool descendInto)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        try
        {
            var info = new DirectoryInfo(directory);
            var files = info.EnumerateFiles(searchPattern, options)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
            var subdirectories = descendInto
                ? info.EnumerateDirectories("*", options)
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .ThenByDescending(d => d.FullName, StringComparer.Ordinal)
                    .Select(d => d.FullName)
                    .ToArray()
                : [];
            return (files, subdirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished or cannot be read is skipped; the rest of the tree still counts.
            return ([], []);
        }
    }
}
