namespace AiUsage.Providers;

/// <summary>
/// Where Claude Code keeps its files on this machine: every existing folder named by
/// <c>CLAUDE_CONFIG_DIR</c> (comma separated), else the two well-known layouts <c>~/.claude</c> and
/// <c>~/.config/claude</c>. Every Claude path in the app derives from this one list.
/// </summary>
internal static class ClaudeConfigRoot
{
    public static IReadOnlyList<string> Candidates() => Candidates(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Pure form of <see cref="Candidates()"/> for a caller (or test) that supplies the
    /// variable's value and the profile folder itself. A value naming no existing folder falls back
    /// to the defaults, which are listed whether or not they exist yet.</summary>
    internal static IReadOnlyList<string> Candidates(string? configDirValue, string userProfile)
    {
        var roots = ConfiguredRoots(configDirValue);
        if (roots.Count == 0)
        {
            roots.Add(Path.Combine(userProfile, ".claude"));
            roots.Add(Path.Combine(userProfile, ".config", "claude"));
        }

        return roots;
    }

    /// <summary>The existing folders <c>CLAUDE_CONFIG_DIR</c> names (comma separated), empty when
    /// the variable is unset or names none.</summary>
    internal static List<string> ConfiguredRoots(string? configDirValue)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        if (string.IsNullOrWhiteSpace(configDirValue))
            return roots;

        foreach (var entry in configDirValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(entry))
                continue;
            var fullPath = Path.GetFullPath(entry);
            if (seen.Add(ResolveLinks(fullPath)))
                roots.Add(fullPath);
        }

        return roots;
    }

    /// <summary>The path with every junction or symbolic link in it replaced by its final target, so
    /// two roots reaching the same folder compare equal and its transcripts are read once. A link that
    /// cannot be resolved stays as it is.</summary>
    internal static string ResolveLinks(string path)
    {
        var current = Path.GetFullPath(path);
        // Each round replaces the deepest link found; a target can itself sit below another link.
        for (var round = 0; round < 8; round++)
        {
            var replaced = false;
            var tail = "";
            for (var probe = new DirectoryInfo(current); probe is not null; probe = probe.Parent)
            {
                if (TryResolveLink(probe) is { } target)
                {
                    current = Path.GetFullPath(Path.Combine(target, tail));
                    replaced = true;
                    break;
                }

                tail = tail.Length == 0 ? probe.Name : Path.Combine(probe.Name, tail);
            }

            if (!replaced)
                break;
        }

        return current;
    }

    private static string? TryResolveLink(DirectoryInfo directory)
    {
        try
        {
            if (!directory.Exists || !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            return directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>projects</c> folders that exist under <paramref name="candidates"/>.</summary>
    internal static List<string> ExistingProjectsRoots(IReadOnlyList<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        foreach (var candidate in candidates)
        {
            var projects = Path.Combine(candidate, "projects");
            if (!Directory.Exists(projects))
                continue;
            var fullPath = Path.GetFullPath(projects);
            if (seen.Add(ResolveLinks(fullPath)))
                roots.Add(fullPath);
        }

        return roots;
    }

    /// <summary>The one projects folder a single-root reader uses: the first that exists, else the
    /// first candidate's (so a diagnostic names a sensible place).</summary>
    internal static string ProjectsRoot(IReadOnlyList<string> candidates) =>
        ExistingProjectsRoots(candidates) is [var first, ..] ? first : Path.Combine(candidates[0], "projects");

    /// <summary>The first candidate folder that holds <paramref name="fileName"/>, else the first
    /// candidate's path for it.</summary>
    internal static string FilePath(IReadOnlyList<string> candidates, string fileName)
    {
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(candidate, fileName);
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(candidates[0], fileName);
    }
}
