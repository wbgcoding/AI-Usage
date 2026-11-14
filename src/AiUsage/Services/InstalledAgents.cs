using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;

namespace AiUsage.Services;

/// <summary>
/// Which coding agents leave a trace on this PC (their own data folder or program), so the very
/// first start shows only those tiles. Looks at folder and file names only, never reads a file.
/// Everything stays reachable through the eye menu afterwards.
/// </summary>
internal static class InstalledAgents
{
    /// <summary>Shown when nothing at all is found, so the first window is never empty.</summary>
    internal const string FallbackProviderId = "claude";

    public static IReadOnlySet<string> Detect() =>
        Detect(DefaultCandidates(), path => Directory.Exists(path) || File.Exists(path), GitHubCliUsage.IsCliInstalled);

    /// <summary>Pure form for tests: a provider counts as installed when any of its candidate paths
    /// exists; Copilot also when the GitHub CLI it reads through is on the PATH.</summary>
    internal static IReadOnlySet<string> Detect(
        IReadOnlyDictionary<string, IReadOnlyList<string>> candidates, Func<string, bool> exists, Func<bool> gitHubCliOnPath)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (providerId, paths) in candidates)
        {
            if (paths.Any(path => path.Length > 0 && exists(path)))
                found.Add(providerId);
        }

        if (gitHubCliOnPath())
            found.Add("copilot");
        return found;
    }

    /// <summary>Makes exactly the detected agents visible (only <see cref="FallbackProviderId"/> when
    /// none was found); every other agent tile starts hidden. Tiles that are no agent stay as they are.</summary>
    public static void ApplyFirstStartVisibility(AppSettings settings, IReadOnlySet<string> detected)
    {
        var shown = detected.Count > 0 ? detected : new HashSet<string>(StringComparer.Ordinal) { FallbackProviderId };
        foreach (var providerId in AppSettings.KnownProviderIds)
        {
            if (settings.Providers.TryGetValue(providerId, out var provider))
                provider.Visible = shown.Contains(providerId);
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> DefaultCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["claude"] = ClaudeConfigRoot.Candidates(),
            ["codex"] = [Path.GetDirectoryName(CodexPaths.Sessions) ?? ""],
            ["cursor"] = [Path.Combine(roaming, "Cursor"), Path.Combine(local, "Programs", "cursor"), Path.Combine(home, ".cursor")],
            ["gemini"] = [Path.Combine(home, ".gemini"), Path.Combine(local, "agy", "bin", "agy.exe")],
            ["copilot"] = [Path.Combine(roaming, "GitHub CLI")],
        };
    }
}
