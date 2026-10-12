using Xunit;

namespace AiUsage.Tests;

/// <summary>A fact that needs node.exe on the PATH: without it the test shows as skipped, never as
/// passed for code it did not run.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (NodeLocator.Find() is null)
            Skip = "node.exe not on PATH";
    }
}

internal static class NodeLocator
{
    /// <summary>The first node executable on the PATH, or null.</summary>
    internal static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (dir.Length > 0 && File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
