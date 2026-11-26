using System.Runtime.CompilerServices;

namespace AiUsage.Tests;

/// <summary>The checkout the tests were compiled from, found through this file's own path.</summary>
internal static class RepoRoot
{
    // The path is captured here, in this file, never at the call site: test files live in layer
    // folders below this one, so a caller's own path would resolve to the wrong directory.
    private static readonly string Root = Resolve();

    public static string Find() => Root;

    private static string Resolve([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
