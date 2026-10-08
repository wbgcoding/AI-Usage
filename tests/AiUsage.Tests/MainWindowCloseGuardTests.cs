using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>
/// MainWindow itself cannot be unit-instantiated (a live WPF Window), so this scans its source as
/// text - the same technique TokenSafetyTests uses - to prove the one invariant Alt+F4 depends on:
/// _reallyClosing is set to true in exactly two places, the tray's own Exit path and the system
/// asking the app to close (an update installing over the running copy, a shutdown, a sign-out), so
/// every other close route keeps hiding into the tray instead of ending the process.
/// </summary>
public class MainWindowCloseGuardTests
{
    [Fact]
    public void ReallyClosing_is_set_true_only_by_the_tray_exit_and_the_systems_own_close_request()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        var assignments = Regex.Matches(source, @"_reallyClosing\s*=\s*true");
        Assert.Equal(2, assignments.Count);

        foreach (var method in new[] { "private void ShutdownFromTray()", "private void Application_SessionEnding(" })
        {
            var methodStart = source.IndexOf(method, StringComparison.Ordinal);
            Assert.True(methodStart >= 0, method + " not found");
            var methodBody = source.Substring(methodStart, Math.Min(300, source.Length - methodStart));
            Assert.Contains("_reallyClosing = true", methodBody, StringComparison.Ordinal);
        }
    }

    /// <summary>The window used to write its position only when a mouse drag ended, so a window moved
    /// any other way - or a process ended by an update installing over it rather than by its own Exit
    /// - restored a position it had long left. Far enough from the real one, that remembered
    /// rectangle sits on no monitor at all and the window opens in the middle of the screen instead
    /// of where the user put it.</summary>
    [Fact]
    public void EveryMoveOfTheWindowWritesItsPositionRatherThanOnlyTheEndOfAMouseDrag()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("LocationChanged += MainWindow_LocationChanged", source, StringComparison.Ordinal);

        var handlerStart = source.IndexOf("private void MainWindow_LocationChanged(", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "MainWindow_LocationChanged not found");
        var handlerBody = source.Substring(handlerStart, Math.Min(600, source.Length - handlerStart));
        Assert.Contains("SaveWindowSettings()", handlerBody, StringComparison.Ordinal);
    }

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }
}
