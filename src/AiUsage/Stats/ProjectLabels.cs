namespace AiUsage.Stats;

/// <summary>
/// The names a set of project paths is shown under: the last folder of each path, with the parent
/// folder added in brackets when two shown projects would otherwise read the same.
/// </summary>
internal static class ProjectLabels
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The last folder of <paramref name="path"/>, or the whole text when it has none (a drive
    /// root, a bare separator).</summary>
    public static string Name(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Separators));
        return name.Length > 0 ? name : path;
    }

    /// <summary>One label per distinct path (case-insensitive). Paths sharing a folder name read
    /// "name (parent)"; everything else is just the name.</summary>
    public static Dictionary<string, string> For(IEnumerable<string> paths)
    {
        var distinct = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var sharing = distinct
            .GroupBy(Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in distinct)
        {
            var name = Name(path);
            labels[path] = sharing.Contains(path) && Parent(path) is { Length: > 0 } parent ? $"{name} ({parent})" : name;
        }
        return labels;
    }

    private static string Parent(string path)
    {
        var directory = Path.GetDirectoryName(path.TrimEnd(Separators));
        if (string.IsNullOrEmpty(directory))
            return "";

        var trimmed = directory.TrimEnd(Separators);
        var parent = Path.GetFileName(trimmed);
        return parent.Length > 0 ? parent : trimmed;
    }
}
