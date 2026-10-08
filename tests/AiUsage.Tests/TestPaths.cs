namespace AiUsage.Tests;

/// <summary>Every test-owned temp path lives under one shared root, so <see cref="TestPathsCleanupFixture"/>
/// can find and remove the whole tree again once the tests that create them have finished. No other
/// test source file may reach into the system temp folder directly - a guard test proves it.</summary>
public static class TestPaths
{
    /// <summary>The folder every test process keeps its own root under.</summary>
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "AI-Usage-tests");

    /// <summary>The one root every test-owned temp path of this process lives under. One folder per
    /// process: two test runs on the same machine at once (two terminals, a CI step beside a local
    /// run) would otherwise sweep each other's files mid-test.</summary>
    public static readonly string Root = Path.Combine(Parent, Environment.ProcessId.ToString());

    // Runs once, before this type's first use from any thread - with the suite's classes no longer
    // forced into one fixed order, a class using only GetPath (which names a path but creates
    // nothing) could otherwise be the very first thing to touch Root, and find it missing. The sweep
    // of earlier runs' leftovers happens right here, once per process before any test can create a
    // file under Root - a collection fixture's Dispose() runs far too late for that: other, parallel
    // collections are still creating and reading files under this same root while one collection
    // finishes, so a sweep there would delete a sibling collection's files mid-test (see
    // TestPathsCleanupFixture). Only the roots of processes that are gone are swept; a root whose
    // process still runs belongs to a concurrent test run and is left alone.
    static TestPaths()
    {
        Directory.CreateDirectory(Parent);
        foreach (var leftover in Directory.EnumerateDirectories(Parent))
        {
            if (!int.TryParse(Path.GetFileName(leftover), out var pid) || (pid != Environment.ProcessId && IsRunning(pid)))
                continue;

            try { Directory.Delete(leftover, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Directory.CreateDirectory(Root);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>An unused path under <see cref="Root"/>, named but not created - for a test naming a
    /// file or directory that must not exist yet, or one that creates the file itself.
    /// <paramref name="extension"/> can add a suffix such as ".vscdb".</summary>
    public static string GetPath(string prefix, string extension = "") =>
        Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}{extension}");

    /// <summary>Creates and returns a fresh directory under <see cref="Root"/>.</summary>
    public static string CreateDirectory(string prefix)
    {
        var path = GetPath(prefix);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Same as <see cref="CreateDirectory"/>, but returns a disposable wrapper that deletes
    /// the directory again right away, swallowing IO errors - for a test that wants its own cleanup
    /// instead of waiting for <see cref="TestPathsCleanupFixture"/> to sweep <see cref="Root"/> at the
    /// end.</summary>
    public static DisposableTestDirectory CreateDisposableDirectory(string prefix) => new(CreateDirectory(prefix));
}

/// <summary>A directory under <see cref="TestPaths.Root"/> that deletes itself on disposal, swallowing
/// IO errors - a file something else still has open is simply left for the next sweep to retry.</summary>
public readonly struct DisposableTestDirectory(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static implicit operator string(DisposableTestDirectory directory) => directory.Path;
}
