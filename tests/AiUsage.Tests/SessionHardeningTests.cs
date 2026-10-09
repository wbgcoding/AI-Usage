using System.Text.Json;
using AiUsage.Providers;
using AiUsage.Views;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The hardening around the hidden session and the sign-in window: what a provider's page can
/// and cannot make them show, wait for, or reach.</summary>
public class SessionHardeningTests
{
    private static string? Label(string email)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { email }));
        return WebUsageSource.ReadAccountLabel(document.RootElement);
    }

    [Theory]
    [InlineData("pat@example.com")]
    [InlineData("pat.lee+tag@sub.example.co.uk")]
    [InlineData("j\u00F6rg@example.com")]
    public void A_plain_address_is_accepted_as_the_account_label(string email) => Assert.Equal(email, Label(email));

    [Theory]
    [InlineData("pat\u202E@example.com", "right-to-left override")]
    [InlineData("pat\u2066@example.com", "left-to-right isolate")]
    [InlineData("pat\u200B@example.com", "zero width space")]
    [InlineData("pat\u200F@example.com", "right-to-left mark")]
    [InlineData("pat\n@example.com", "line break")]
    [InlineData("pat\t@example.com", "tab")]
    [InlineData("pat\u0000@example.com", "null")]
    [InlineData("pat\u0085@example.com", "next line")]
    [InlineData("pat\u2028@example.com", "line separator")]
    [InlineData("pat\uE000@example.com", "private use")]
    public void An_address_with_a_control_or_direction_character_is_not_a_label(string email, string because)
    {
        Assert.True(Label(email) is null, because);
    }

    [Fact]
    public void An_address_without_an_at_sign_or_over_the_length_limit_is_still_not_a_label()
    {
        Assert.Null(Label("no-at-sign"));
        Assert.Null(Label(new string('a', 250) + "@example.com"));
    }

    private static readonly string[] ProviderHosts = ["cursor.com"];

    [Fact]
    public void The_title_is_plain_on_the_providers_own_hosts_and_before_any_page_is_known()
    {
        Assert.Equal("Sign in", SignInWindow.TitleFor("Sign in", null, ProviderHosts, ["auth.example"]));
        Assert.Equal("Sign in", SignInWindow.TitleFor("Sign in", "https://www.cursor.com/login", ProviderHosts, ["auth.example"]));
        Assert.Equal("Sign in", SignInWindow.TitleFor("Sign in", "not a url", ProviderHosts, ["auth.example"]));
    }

    [Fact]
    public void The_title_names_the_real_host_while_a_host_the_person_allowed_is_loaded()
    {
        Assert.Equal(
            "Sign in \u00B7 auth.example",
            SignInWindow.TitleFor("Sign in", "https://auth.example/path?x=1", ProviderHosts, ["auth.example"]));
    }

    [Fact]
    public void The_title_shows_an_international_host_in_its_ascii_form_so_a_look_alike_is_visible()
    {
        // Cyrillic a in place of the Latin one.
        var ascii = new System.Globalization.IdnMapping().GetAscii("\u0430uth.example");

        var title = SignInWindow.TitleFor("Sign in", "https://\u0430uth.example/", ProviderHosts, [ascii]);

        Assert.StartsWith("Sign in \u00B7 xn--", title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_host_is_cut_at_the_front_so_its_end_stays_readable()
    {
        var host = string.Join('.', Enumerable.Repeat("sub", 30)) + ".evil.example";

        var title = SignInWindow.TitleFor("Sign in", "https://" + host + "/", ProviderHosts, [host]);

        Assert.EndsWith(".evil.example", title, StringComparison.Ordinal);
        Assert.Contains("\u2026", title, StringComparison.Ordinal);
        Assert.True(title.Length <= "Sign in \u00B7 ".Length + 48);
    }

    [Fact]
    public void The_title_never_uses_text_the_page_controls()
    {
        var root = FindRepoRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "SignInWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "SignInWindow.xaml"));

        Assert.DoesNotContain("DocumentTitle", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;", code, StringComparison.Ordinal);
        Assert.Contains("TitleHostFor(Browser.CoreWebView2?.Source,", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://auth.example/login", true)]
    [InlineData("https://AUTH.example/", true)]
    [InlineData("https://sub.auth.example/", false)]
    [InlineData("https://auth.example.evil.test/", false)]
    [InlineData("http://auth.example/", false)]
    [InlineData("https://other.example/", false)]
    public void What_the_person_allowed_by_hand_is_that_one_host_and_nothing_around_it(string uri, bool expected)
    {
        Assert.Equal(expected, SignInWindow.IsAllowed(uri, ProviderHosts, ["auth.example"]));
    }

    [Fact]
    public void An_allowed_host_changes_neither_the_providers_list_nor_any_other_windows_answer()
    {
        IReadOnlyList<string> otherProviderHosts = ["claude.ai"];
        List<string> providerHosts = ["cursor.com"];
        List<string> allowedByThisWindow = ["auth.example"];

        Assert.True(SignInWindow.IsAllowed("https://auth.example/", providerHosts, allowedByThisWindow));

        // The list the person built belongs to the one window; the provider's list stays as it was, and
        // another window starts with an empty one.
        Assert.Equal(["cursor.com"], providerHosts);
        Assert.False(SignInWindow.IsAllowed("https://auth.example/", otherProviderHosts, []));
        Assert.False(SignInWindow.IsAllowed("https://auth.example/", providerHosts, []));
    }

    [Fact]
    public void The_hidden_session_only_ever_sees_the_providers_own_list()
    {
        var root = FindRepoRoot();
        var runner = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Web", "WebSessionScriptRunner.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "SignInWindow.xaml.cs"));

        // The hand-made list exists in the sign-in window alone, as a list of its own.
        Assert.DoesNotContain("AllowedByUser", runner, StringComparison.Ordinal);
        Assert.Contains("private readonly List<string> _hostsAllowedByUser = [];", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_allowedHosts.Add", window, StringComparison.Ordinal);
        Assert.DoesNotContain("static readonly List<string> _hostsAllowedByUser", window, StringComparison.Ordinal);
        Assert.DoesNotContain("static List<string> _hostsAllowedByUser", window, StringComparison.Ordinal);
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

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task A_cancel_after_the_installer_started_does_not_end_the_wait()
    {
        using var cts = new CancellationTokenSource();
        // About two seconds of work; the cancel lands well inside it.
        var run = WebViewRuntimeInstaller.RunProcessAsync(Cmd, "/c ping -n 3 127.0.0.1 >nul", TimeSpan.FromSeconds(60), cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();

        var exitCode = await run;

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task A_cancel_before_the_installer_starts_still_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WebViewRuntimeInstaller.RunProcessAsync(Cmd, "/c exit 0", TimeSpan.FromSeconds(60), cts.Token));
    }

    [Fact]
    public async Task An_installer_that_overruns_its_time_limit_is_a_failure_not_a_cancel()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => WebViewRuntimeInstaller.RunProcessAsync(Cmd, "/c ping -n 30 127.0.0.1 >nul", TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }
}
