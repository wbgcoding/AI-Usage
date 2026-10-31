using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Views;
using AiUsage.Web;

namespace AiUsage.Tests;

public class SignInDetectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T10:00:00Z");

    [Theory]
    [InlineData("""{"status":"not_signed_in"}""", "NotSignedIn")]
    [InlineData("""{"status":"ok","results":[]}""", "SignedIn")]
    [InlineData("""{"status":"blocked"}""", "Unknown")]
    [InlineData("""{"status":"failed"}""", "Unknown")]
    [InlineData("""{"offOrigin":"https://accounts.google.com/"}""", "Unknown")]
    [InlineData("""[]""", "Unknown")]
    [InlineData("not json", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void The_session_check_counts_only_a_clear_answer(string? envelope, string expected) =>
        Assert.Equal(Enum.Parse<SignInSessionState>(expected), SignInSessionCheck.Read(envelope));

    [Theory]
    // A login page that bounced straight back on a session the site rejects: start over once without it.
    [InlineData("NotSignedIn", true, false, "RetryWithoutSession")]
    [InlineData("NotSignedIn", true, true, "StayOpen")]
    // Mid-flow on a page that only looks finished: the window stays open.
    [InlineData("NotSignedIn", false, false, "StayOpen")]
    [InlineData("SignedIn", true, false, "Finish")]
    [InlineData("SignedIn", false, false, "Finish")]
    // No clear answer keeps the address-based verdict.
    [InlineData("Unknown", false, false, "Finish")]
    [InlineData("Unknown", true, false, "Finish")]
    public void A_landing_finishes_only_when_the_site_does_not_reject_the_session(
        string state, bool quickBounce, bool alreadyCleared, string expected) =>
        Assert.Equal(
            Enum.Parse<SignInLandingStep>(expected),
            SignInWindow.NextStep(Enum.Parse<SignInSessionState>(state), quickBounce, alreadyCleared));

    [Fact]
    public void A_real_sign_in_gets_more_time_to_settle_than_a_quick_bounce()
    {
        Assert.True(SignInWindow.SessionCheckAttempts(quickBounce: true) >= 2);
        Assert.True(SignInWindow.SessionCheckAttempts(quickBounce: false) * SignInWindow.SessionCheckPause >= TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("https://platform.claude.com/login", 5, true)]
    [InlineData("https://platform.claude.com/login?returnTo=%2Fsettings", 30, true)]
    [InlineData("https://platform.claude.com/login", 1, false)]
    [InlineData("https://platform.claude.com/settings/billing", 30, false)]
    [InlineData("https://accounts.google.com/signin", 30, false)]
    public void The_login_page_is_asked_now_and_then_only_while_it_stands(string page, int secondsOpen, bool expected)
    {
        Assert.Equal(expected, SignInWindow.ShouldPollLoginPage(new Uri(page), "platform.claude.com", TimeSpan.FromSeconds(secondsOpen)));
    }

    [Theory]
    [InlineData("https://platform.claude.com/dashboard", true)]
    [InlineData("https://platform.claude.com/settings/billing", true)]
    [InlineData("https://platform.claude.com/login", false)]
    [InlineData("https://platform.claude.com/auth/google/callback", false)]
    [InlineData("https://accounts.google.com/v3/signin/identifier", false)]
    [InlineData("https://claude.ai/new", false)]
    public void A_popup_that_lands_on_the_provider_app_ends_the_sign_in(string landing, bool expected)
    {
        Assert.Equal(expected, SignInWindow.PopupFinishedOnProvider(new Uri(landing), "platform.claude.com"));
    }

    [Fact]
    public void The_console_sign_in_checks_its_session()
    {
        Assert.NotNull(ProviderRegistry.SessionCheckScriptFor("claude~console"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("claude#2")]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void Providers_with_a_clear_signed_out_answer_check_the_session(string accountKey)
    {
        var script = ProviderRegistry.SessionCheckScriptFor(accountKey);

        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("not_signed_in", script);
    }

    [Fact]
    public void Gemini_signs_in_without_a_session_check() =>
        Assert.Null(ProviderRegistry.SessionCheckScriptFor("gemini"));

    [Theory]
    [InlineData(401, "TokenExpired")]
    [InlineData(403, "NonOkResponse")]
    public void A_refused_local_read_keeps_its_status_code(int status, string reason)
    {
        var denied = ClaudeCodeLogin.DeniedUsage(status);

        Assert.NotNull(denied);
        Assert.Equal(LocalLoginOutcome.NotSignedIn, denied.Outcome);
        Assert.Equal(Enum.Parse<ClaudeCodeLoginReason>(reason), denied.Reason);
        Assert.Equal(status, denied.HttpStatus);
    }

    [Fact]
    public void The_local_login_word_names_reason_kind_and_code_but_never_more()
    {
        var windows = new[] { new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 10, null, null) };

        Assert.Equal("ok", ClaudeProvider.LocalLoginWord(new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows)));
        Assert.Equal("no_token", ClaudeProvider.LocalLoginWord(new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.NoToken)));
        Assert.Equal("token_expired (HTTP 401)", ClaudeProvider.LocalLoginWord(ClaudeCodeLogin.DeniedUsage(401)!));
        Assert.Equal("non_ok_response [RateLimited] (HTTP 429)", ClaudeProvider.LocalLoginWord(new ClaudeCodeUsage(
            LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse, FailureKind: FailureKind.RateLimited, HttpStatus: 429)));
        Assert.Equal("unreadable_file", ClaudeProvider.LocalLoginWord(new ClaudeCodeUsage(LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.Unreadable)));
    }

    [Fact]
    public async Task A_local_login_read_logs_only_when_its_outcome_changes()
    {
        var windows = new[] { new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 10, null, null) };
        var answers = new Queue<ClaudeCodeUsage>([
            new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.NoToken),
            new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.NoToken),
            new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows),
            new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows),
        ]);
        var lines = new List<string>();
        var provider = new ClaudeProvider(
            projectsRoot: Path.Combine(AppContext.BaseDirectory, "does-not-exist"), now: () => Now,
            localLogin: _ => Task.FromResult(answers.Dequeue()), readAccountLabel: () => null)
        {
            LocalLoginLog = lines.Add,
        };

        for (var i = 0; i < 4; i++)
            await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(["Claude Code sign-in read: no_token.", "Claude Code sign-in read: ok."], lines);
    }

    [Fact]
    public async Task A_rewritten_sign_in_file_calls_back_once_per_burst()
    {
        var folder = Directory.CreateTempSubdirectory("aiusage-signin-watch-").FullName;
        try
        {
            var calls = 0;
            using var watcher = new ClaudeCredentialsWatcher([folder], "signin.json", () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(200));
            Assert.Equal(1, watcher.WatchedFolderCount);

            var file = Path.Combine(folder, "signin.json");
            File.WriteAllText(file, "{}");
            File.WriteAllText(file, "{\"a\":1}");
            File.WriteAllText(Path.Combine(folder, "other.json"), "{}");

            await TestWait.UntilAsync(() => Volatile.Read(ref calls) > 0);
            await Task.Delay(600);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Another_file_in_the_folder_and_a_disposed_watcher_call_nothing()
    {
        var folder = Directory.CreateTempSubdirectory("aiusage-signin-watch-").FullName;
        try
        {
            var calls = 0;
            var watcher = new ClaudeCredentialsWatcher([folder, Path.Combine(folder, "missing")], "signin.json",
                () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, watcher.WatchedFolderCount);

            File.WriteAllText(Path.Combine(folder, "other.json"), "{}");
            await Task.Delay(500);
            Assert.Equal(0, Volatile.Read(ref calls));

            watcher.Dispose();
            File.WriteAllText(Path.Combine(folder, "signin.json"), "{}");
            await Task.Delay(500);
            Assert.Equal(0, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
