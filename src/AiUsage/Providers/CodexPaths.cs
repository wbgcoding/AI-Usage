namespace AiUsage.Providers;

/// <summary>Where the Codex command line tool keeps its session logs; the provider reads the live
/// folder for its rate limits and the statistics index reads both.</summary>
internal static class CodexPaths
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string Sessions => Path.Combine(Root, "sessions");

    public static string ArchivedSessions => Path.Combine(Root, "archived_sessions");
}
