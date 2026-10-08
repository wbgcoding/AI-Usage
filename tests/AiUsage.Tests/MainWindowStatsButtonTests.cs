using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>
/// MainWindow itself cannot be unit-instantiated (a live WPF Window, see
/// <see cref="MainWindowCloseGuardTests"/>) - this scans its source as text, the same technique, to
/// prove the stats window opens as a real singleton: exactly one place creates a new
/// StatsWindow, guarded by a null check on the field that remembers it, and the Show()/Activate()
/// calls that follow sit outside that guard - so a second open request, with the field already set,
/// skips creation entirely and only brings the existing window forward.
/// </summary>
public class MainWindowStatsButtonTests
{
    [Fact]
    public void A_second_open_request_focuses_the_existing_stats_window_instead_of_creating_a_second_one()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        var creations = Regex.Matches(source, @"new StatsWindow\(");
        Assert.Single(creations);

        var methodStart = source.IndexOf("private void TitleBarControl_StatsRequested(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "TitleBarControl_StatsRequested(...) not found");
        var methodEnd = FindMatchingBraceEnd(source, source.IndexOf('{', methodStart));
        var methodBody = source[methodStart..methodEnd];

        Assert.Contains("_statsWindow is null", methodBody, StringComparison.Ordinal);
        Assert.Contains("new StatsWindow(", methodBody, StringComparison.Ordinal);

        var showIndex = methodBody.IndexOf("_statsWindow.Show();", StringComparison.Ordinal);
        var activateIndex = methodBody.IndexOf("_statsWindow.Activate();", StringComparison.Ordinal);
        Assert.True(showIndex >= 0, "_statsWindow.Show() not found");
        Assert.True(activateIndex > showIndex, "_statsWindow.Activate() must follow Show()");

        // Both calls must sit outside (after) the if-block that creates the window - inside it, a
        // second call with the field already set would do nothing at all, never focusing anything.
        var ifIndex = methodBody.IndexOf("if (_statsWindow is null)", StringComparison.Ordinal);
        Assert.True(ifIndex >= 0, "if (_statsWindow is null) guard not found");
        var ifBlockEnd = FindMatchingBraceEnd(methodBody, methodBody.IndexOf('{', ifIndex));
        Assert.True(showIndex > ifBlockEnd, "Show() must run after the creation guard, not inside it");
    }

    private static int FindMatchingBraceEnd(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        throw new InvalidOperationException("No matching closing brace found");
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
