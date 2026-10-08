using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

public class SignInWindowTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LoadingIndicatorShowsOnlyBeforeTheFirstNavigationCompletes(bool firstNavigationCompleted, bool expectedVisible) =>
        Assert.Equal(expectedVisible, SignInWindow.ShouldShowLoadingIndicator(firstNavigationCompleted));

    [Theory]
    // The provider's own site, away from any login path: this is where a finished sign-in lands.
    [InlineData("https://chatgpt.com/", "chatgpt.com", false)]
    [InlineData("https://chatgpt.com/codex/settings/usage", "chatgpt.com", false)]
    [InlineData("https://claude.ai/new", "claude.ai", false)]
    // Login paths on the provider's own site, spelled the way each provider spells them.
    [InlineData("https://claude.ai/login", "claude.ai", true)]
    [InlineData("https://chatgpt.com/auth/login", "chatgpt.com", true)]
    [InlineData("https://auth.openai.com/log-in", "chatgpt.com", true)]
    // Identity providers the login passes through, including the hosts a two-factor confirmation
    // adds - anything that is not the provider's own site counts, so no further host has to be named.
    [InlineData("https://accounts.google.com/signin/v2/challenge", "chatgpt.com", true)]
    [InlineData("https://gds.google.com/web/verify", "chatgpt.com", true)]
    [InlineData("https://appleid.apple.com/auth/authorize", "chatgpt.com", true)]
    public void LoginAndIdentityProviderPagesAreToldApartFromTheProvidersOwnPages(string url, string providerHost, bool expected) =>
        Assert.Equal(expected, SignInWindow.IsOnLoginOrOAuthPage(new Uri(url), providerHost));

    [Theory]
    // A page the user was trying to reach: the notice is the only thing that makes the block visible.
    [InlineData("https://elsewhere.example/login", true)]
    [InlineData("http://elsewhere.example/login", true)]
    // The blank page a redirected pop-up loads before its real address, and a handover to another
    // program: cancelled either way, but a notice here would cover the login form over nothing the
    // user did. "Sign in with Google" opens exactly such a pop-up.
    [InlineData("about:blank", false)]
    [InlineData("mailto:hilfe.at.elsewhere.example", false)]
    [InlineData("cursor://anysphere.cursor-deeplink/auth", false)]
    [InlineData("", false)]
    public void OnlyARealPageTheUserWasHeadingForIsWorthABlockedNotice(string uri, bool expected) =>
        Assert.Equal(expected, SignInWindow.IsWorthExplaining(uri));

    [Theory]
    [InlineData("https://elsewhere.example/login", true)]
    [InlineData("http://elsewhere.example/login", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    public void OnlyAnHttpsAddressIsOfferedTheLoadAnywayButton(string uri, bool expected) =>
        Assert.Equal(expected, SignInWindow.CanAllow(uri));

    [Theory]
    // On the provider's own list: allowed without the user having to decide anything.
    [InlineData("https://cursor.com/dashboard", true)]
    // Only on the list the user built on this window's blocked notice: allowed, because they were
    // shown the host and said yes to it.
    [InlineData("https://authenticator.cursor.sh/", true)]
    // On neither: this is what the notice is for.
    [InlineData("https://elsewhere.example/login", false)]
    // The https rule holds for the user's own list too - a host they allowed still never carries the
    // session's cookies over plain http.
    [InlineData("http://authenticator.cursor.sh/", false)]
    public void AHostTheUserAllowedOnTheNoticeCountsAsAllowedForThatWindowOnly(string uri, bool expected) =>
        Assert.Equal(expected, SignInWindow.IsAllowed(uri, ["cursor.com"], ["authenticator.cursor.sh"]));

    [Theory]
    // Bounced straight back off the login page, never through a host other than the provider's own:
    // the session in this window's shared browser profile was already signed in.
    [InlineData(true, false, 0.2, true)]
    // A real sign-in passes through an identity provider's own host on the way, even a fast one.
    [InlineData(true, true, 0.2, false)]
    // Took more than two seconds: a real login (typing, a 2FA step), not an automatic bounce.
    [InlineData(true, false, 2.5, false)]
    // Never reached a login page at all - nothing here to call already signed in.
    [InlineData(false, false, 0.2, false)]
    public void AlreadySignedInIsDetectedOnlyForAFastReturnWithNoForeignHost(
        bool leftLoginPage, bool sawForeignHost, double secondsSinceOpened, bool expected) =>
        Assert.Equal(expected, SignInWindow.IsAlreadySignedIn(leftLoginPage, sawForeignHost, TimeSpan.FromSeconds(secondsSinceOpened)));

    /// <summary>A subdomain of the provider's own site is still the provider's own site - otherwise
    /// every such page would count as an identity provider's and close the window early.</summary>
    [Fact]
    public void ASubdomainOfTheProvidersOwnSiteIsNotAnIdentityProvider() =>
        Assert.False(SignInWindow.IsOnLoginOrOAuthPage(new Uri("https://www.claude.ai/new"), "claude.ai"));

    /// <summary>The page is a native child window and WPF content is never composited over one, so a
    /// notice that does not hide the page is invisible in exactly the case it exists for. The window
    /// itself cannot be unit-instantiated (same reason as WindowTitleBindingTests), so this reads the
    /// source: showing the notice collapses the page, dismissing it brings the page back.</summary>
    [Fact]
    public void TheBlockedNoticeHidesThePageAndTheDismissBringsItBack()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "AiUsage", "Views", "SignInWindow.xaml.cs"));

        var show = Body(source, "private void ShowBlockedNotice(");
        Assert.Contains("Browser.Visibility = Visibility.Collapsed;", show, StringComparison.Ordinal);
        Assert.Contains("BlockedPanel.Visibility = Visibility.Visible;", show, StringComparison.Ordinal);

        var dismiss = Body(source, "private void DismissBlockedNotice_Click(");
        Assert.Contains("Browser.Visibility = Visibility.Visible;", dismiss, StringComparison.Ordinal);
        Assert.Contains("BlockedPanel.Visibility = Visibility.Collapsed;", dismiss, StringComparison.Ordinal);
    }

    /// <summary>The notice's safe choice ("Got it") comes first and carries the prominent style; the
    /// way past the allow-list ("Load the page anyway") follows as the quiet secondary button. Read
    /// from the window's XAML, since the window itself cannot be instantiated here.</summary>
    [Fact]
    public void BlockedNoticePutsTheSafeChoiceFirst()
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(
            FindRepoRoot(), "src", "AiUsage", "Views", "SignInWindow.xaml"));
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var panel = document.Descendants(presentation + "StackPanel")
            .Single(e => (string?)e.Attribute(xaml + "Name") == "BlockedPanel");
        var buttons = panel.Descendants(presentation + "Button").ToList();

        Assert.Equal(2, buttons.Count);
        Assert.Equal("DismissBlockedNotice_Click", (string?)buttons[0].Attribute("Click"));
        Assert.Equal("{StaticResource AiUsageButton}", (string?)buttons[0].Attribute("Style"));
        Assert.Equal("AllowBlockedHost_Click", (string?)buttons[1].Attribute("Click"));
        Assert.Equal("{StaticResource AiUsageQuietButton}", (string?)buttons[1].Attribute("Style"));
    }

    /// <summary>The pointer must stay visible over every window of the app while the notice stands and
    /// after it is gone. The page is a native window that can leave a cursor request of its own
    /// standing once it is hidden, so showing, dismissing, allowing and closing all hand the cursor
    /// back to WPF, and the notice area names an arrow itself.</summary>
    [Fact]
    public void TheBlockedNoticeHandsThePointerBackToTheApp()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "SignInWindow.xaml.cs"));

        Assert.Contains("CursorRestore.Reset();", WholeMember(source, "private void ShowBlockedNotice("), StringComparison.Ordinal);
        Assert.Contains("CursorRestore.Reset();", WholeMember(source, "private void DismissBlockedNotice_Click("), StringComparison.Ordinal);
        Assert.Contains("CursorRestore.Reset();", WholeMember(source, "private void AllowBlockedHost_Click("), StringComparison.Ordinal);
        Assert.Contains("CursorRestore.Reset();", WholeMember(source, "private void OnClosed("), StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "SignInWindow.xaml"));
        Assert.Contains("<Grid Cursor=\"Arrow\">", xaml, StringComparison.Ordinal);
    }

    /// <summary>Nothing in the app may leave an override standing: with one set, every window shows
    /// that cursor (or none) no matter what is under the pointer.</summary>
    [Fact]
    public void NoAppCodeSetsAStandingCursorOverride()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src", "AiUsage"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("Mouse.OverrideCursor = ", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => name != "CursorRestore.cs")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ResetClearsAnOverrideAndNeverThrowsWithoutAWindow()
    {
        Exception? failure = null;
        System.Windows.Input.Cursor? after = System.Windows.Input.Cursors.None;
        var worker = new Thread(() =>
        {
            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
                AiUsage.Services.CursorRestore.Reset();
                after = System.Windows.Input.Mouse.OverrideCursor;
            }
            catch (Exception ex) { failure = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)));

        Assert.Null(failure);
        Assert.Null(after);
    }

    /// <summary>A whole member, from its signature to the closing brace at member indent.</summary>
    private static string WholeMember(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found in SignInWindow.xaml.cs");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }

    /// <summary>Everything from a member's signature up to the blank line that ends it - enough to
    /// read one short method without a parser, and it fails loudly if the member is renamed.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found in SignInWindow.xaml.cs");
        var end = source.IndexOf("\n\n", start, StringComparison.Ordinal);
        if (end < 0)
            end = source.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

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

    [Fact]
    public void The_blocked_navigation_detail_masks_ids_in_the_path_and_drops_the_query()
    {
        Assert.Equal(
            "https://login.example.com/org/{id}/callback",
            SignInWindow.BlockedDetail("https://login.example.com/org/2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc/callback?code=abc123"));
        Assert.Equal("https://login.example.com/oauth/{id}", SignInWindow.BlockedDetail("https://login.example.com/oauth/A1B2C3D4E5F6G7H8I9"));
    }
}
