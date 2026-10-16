namespace AiUsage.Providers;

/// <summary>Where the Codex command line tool keeps its session logs; the provider reads the live
/// folder for its rate limits and the statistics index reads both.</summary>
internal static class CodexPaths
{
    private static string Root => ResolveRoot(
        Environment.GetEnvironmentVariable("CODEX_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);

    /// <summary>Codex itself keeps everything under CODEX_HOME when that is set; ~/.codex otherwise,
    /// and also when the variable names a folder that does not exist.</summary>
    internal static string ResolveRoot(string? codexHome, string userProfile, Func<string, bool> folderExists) =>
        !string.IsNullOrWhiteSpace(codexHome) && folderExists(codexHome.Trim())
            ? codexHome.Trim()
            : Path.Combine(userProfile, ".codex");

    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>The live folder as the About page names it: a shorthand, never the user's own path.</summary>
    public static string SessionsDisplay => DisplaySessions(Environment.GetEnvironmentVariable("CODEX_HOME"), Directory.Exists);

    internal static string DisplaySessions(string? codexHome, Func<string, bool> folderExists) =>
        !string.IsNullOrWhiteSpace(codexHome) && folderExists(codexHome.Trim()) ? "%CODEX_HOME%/sessions" : "~/.codex/sessions";

    public static string ArchivedSessions => Path.Combine(Root, "archived_sessions");
}
