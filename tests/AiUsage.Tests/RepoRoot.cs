using System.Runtime.CompilerServices;

namespace AiUsage.Tests;

/// <summary>The checkout the tests were compiled from, found through this file's own path.</summary>
internal static class RepoRoot
{
    public static string Find([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
