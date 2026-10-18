namespace AiUsage.Stats;

/// <summary>
/// Which project paths of the index are one project that moved: worked out at query time from the
/// stored paths, never written back, so the index keeps the raw path of every row.
/// </summary>
public sealed class ProjectAliases
{
    public static readonly ProjectAliases None = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _target;
    private readonly Dictionary<string, IReadOnlyList<string>> _former;

    internal ProjectAliases(Dictionary<string, string> target, Dictionary<string, IReadOnlyList<string>> former)
    {
        _target = target;
        _former = former;
    }

    public bool IsEmpty => _target.Count == 0;

    /// <summary>The path <paramref name="project"/> continues as; the path itself when it is not merged.</summary>
    public string Resolve(string project)
    {
        if (_target.Count == 0 || string.IsNullOrEmpty(project))
            return project;

        return _target.TryGetValue(StatsAggregator.ProjectKey(project), out var target) ? target : project;
    }

    /// <summary>The older paths merged into <paramref name="project"/>, empty when none.</summary>
    public IReadOnlyList<string> FormerPaths(string project) =>
        !string.IsNullOrEmpty(project) && _former.TryGetValue(StatsAggregator.ProjectKey(project), out var paths) ? paths : [];

    /// <summary>The records with every merged path replaced; the same list when nothing changes.</summary>
    public IReadOnlyList<StatsRecord> Apply(IReadOnlyList<StatsRecord> records)
    {
        if (_target.Count == 0)
            return records;

        List<StatsRecord>? result = null;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var resolved = Resolve(record.Project);
            if (result is null && string.Equals(resolved, record.Project, StringComparison.Ordinal))
                continue;

            if (result is null)
            {
                result = new List<StatsRecord>(records.Count);
                for (var j = 0; j < i; j++)
                    result.Add(records[j]);
            }

            result.Add(string.Equals(resolved, record.Project, StringComparison.Ordinal) ? record : record with { Project = resolved });
        }

        return result ?? records;
    }

    /// <summary>The sessions with every merged path replaced; the same list when nothing changes.</summary>
    public IReadOnlyList<StatsSessionRecord> Apply(IReadOnlyList<StatsSessionRecord> sessions)
    {
        if (_target.Count == 0)
            return sessions;

        List<StatsSessionRecord>? result = null;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var resolved = Resolve(session.Project);
            var same = string.Equals(resolved, session.Project, StringComparison.Ordinal);
            if (result is null && same)
                continue;

            if (result is null)
            {
                result = new List<StatsSessionRecord>(sessions.Count);
                for (var j = 0; j < i; j++)
                    result.Add(sessions[j]);
            }

            result.Add(same ? session : session with { Project = resolved });
        }

        return result ?? sessions;
    }
}

/// <summary>
/// Finds projects that moved folders. Full paths are grouped by their last folder name
/// (case-insensitive). A path inside another indexed project only pairs with paths inside that same
/// project, so unrelated subfolders of one name stay apart. Within a pairing, paths whose folder no
/// longer exists merge into the single one that does; with none left, into the most recently used;
/// with two or more present they stay apart. A bare folder name (older rows carry no path) continues
/// as the project of that name when only one is left.
/// </summary>
public static class ProjectAliasResolver
{
    /// <summary>Builds the aliases for the given rows. <paramref name="directoryExists"/> is asked once per
    /// path of a group with several paths, so it may touch the disk: call this off the UI thread.</summary>
    public static ProjectAliases Build(
        IReadOnlyList<StatsRecord> records, IReadOnlyList<StatsSessionRecord> sessions, Func<string, bool> directoryExists)
    {
        // Latest use per project root, over the usage rows and the sessions.
        var lastUsed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (IsFullPath(record.Project))
                Touch(lastUsed, record.Project, record.Day.ToDateTime(new TimeOnly(Math.Clamp(record.Hour, 0, 23), 0)));
        }

        foreach (var session in sessions)
        {
            if (IsFullPath(session.Project))
                Touch(lastUsed, session.Project, session.LastUtc.ToLocalTime().DateTime);
        }

        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var former = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var groups = lastUsed.Keys
            .GroupBy(ProjectLabels.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key, group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var existence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Exists(string path)
        {
            if (!existence.TryGetValue(path, out var found))
                existence[path] = found = directoryExists(path);
            return found;
        }

        string WinnerOf(string path) => target.TryGetValue(path, out var winner) ? winner : path;

        // A group is settled after the groups of the project roots its paths sit in, so a path inside
        // a root only pairs with one inside the same (already merged) root.
        void Settle(string name)
        {
            if (settled.Contains(name) || !running.Add(name))
                return;

            var paths = groups[name];
            var roots = paths.ToDictionary(path => path, path => EnclosingRoot(lastUsed, path), StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Values)
            {
                if (root is not null)
                    Settle(ProjectLabels.Name(root));
            }

            foreach (var place in paths.GroupBy(path => roots[path] is { } root ? WinnerOf(root) : "", StringComparer.OrdinalIgnoreCase))
            {
                var members = place.ToList();
                if (members.Count < 2)
                    continue;

                var present = members.Where(Exists).ToList();
                string winner;
                if (present.Count == 1)
                    winner = present[0];
                else if (present.Count == 0)
                    winner = members.OrderByDescending(path => lastUsed[path]).First();
                else
                    continue;

                var others = members.Where(path => !string.Equals(path, winner, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var other in others)
                    target[other] = winner;
                former[winner] = others;
            }

            running.Remove(name);
            settled.Add(name);
        }

        foreach (var name in groups.Keys.ToList())
            Settle(name);

        // A bare folder name follows its project when only one project of that name is left.
        var bareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record.Project.Length > 0 && !IsFullPath(record.Project))
                bareNames.Add(record.Project);
        }

        foreach (var session in sessions)
        {
            if (session.Project.Length > 0 && !IsFullPath(session.Project))
                bareNames.Add(session.Project);
        }

        foreach (var bare in bareNames)
        {
            if (!groups.TryGetValue(bare, out var paths))
                continue;

            var survivors = paths.Select(WinnerOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (survivors.Count == 1)
                target[bare] = survivors[0];
        }

        return target.Count == 0 ? ProjectAliases.None : new ProjectAliases(target, former);
    }

    /// <summary>Builds the aliases against the real file system.</summary>
    public static ProjectAliases Build(IReadOnlyList<StatsRecord> records, IReadOnlyList<StatsSessionRecord> sessions) =>
        Build(records, sessions, SafeDirectoryExists);

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Touch(Dictionary<string, DateTime> lastUsed, string project, DateTime when)
    {
        var key = StatsAggregator.ProjectKey(project);
        if (!lastUsed.TryGetValue(key, out var known) || when > known)
            lastUsed[key] = when;
    }

    /// <summary>The nearest other indexed project folder that contains <paramref name="path"/>, or null.</summary>
    private static string? EnclosingRoot(Dictionary<string, DateTime> roots, string path)
    {
        var end = path.LastIndexOfAny(Separators);
        while (end > 0)
        {
            var parent = path[..end];
            if (roots.ContainsKey(parent))
                return parent;
            end = parent.LastIndexOfAny(Separators);
        }

        return null;
    }

    private static readonly char[] Separators = ['\\', '/'];

    private static bool IsFullPath(string project) => project.AsSpan().IndexOfAny('\\', '/') >= 0;
}
