namespace AiUsage.Tests;

/// <summary>
/// MainWindow itself cannot be unit-instantiated (a live WPF Window, see
/// MainWindowCloseGuardTests for the same technique), so this scans its source as text to prove the
/// fix holds: the sign-in flow opens whichever tile's own account it was raised for, never a single
/// fixed session shared by every tile - the bug that would otherwise sign a second Claude account
/// into the first account's browser profile.
/// </summary>
public class SignInDescriptorTests
{
    [Fact]
    public void SignInFlow_opens_the_tiles_own_account_not_a_fixed_session()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        // The old, fixed single-session descriptor must be gone entirely - ProviderRegistry.WebSessionFor
        // (per-account) replaces it, see ProviderRegistry.cs.
        Assert.DoesNotContain("ClaudeWebSession", source, StringComparison.Ordinal);

        var callIndex = source.IndexOf("WebSignInFlow.Open(", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "WebSignInFlow.Open(...) not found in MainWindow.xaml.cs");
        var call = source.Substring(callIndex, Math.Min(200, source.Length - callIndex));
        Assert.Contains("WebSessionFor(accountKey)", call, StringComparison.Ordinal);

        // SignOutAsync/ShowSignOutIncomplete must route by the same per-tile account key, not a
        // provider-wide id that two accounts of the same provider would share.
        Assert.Contains("SignOutAsync(accountKey)", source, StringComparison.Ordinal);
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
