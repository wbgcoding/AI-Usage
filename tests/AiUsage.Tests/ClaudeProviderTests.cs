using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Tests;

public class ClaudeProviderTests : IDisposable
{
    private static readonly ClaudeUsageEndpoint ClaudeEndpoint = new();

    /// <summary>A provider whose local-login route is the one under test and whose local transcript
    /// scan can find nothing. The scan runs first by design (a hit rate limit must show even while
    /// signed out), so a provider pointed at the real ~/.claude/projects reports whatever the machine
    /// happens to hold - which made these tests pass or fail depending on what the user did with
    /// Claude Code that hour.</summary>
    private static ClaudeProvider LocalLoginProviderWithoutTranscripts(
        Func<CancellationToken, Task<ClaudeCodeUsage>> localLogin) => new(
        projectsRoot: Path.Combine(FixturesDirectory, "does-not-exist"), now: null, webSource: null,
        settings: null, saveSettings: null, localLogin: localLogin);

    [Fact]
    public void A_local_login_account_holds_the_remote_refresh_floor_not_the_per_tick_default()
    {
        var provider = new ClaudeProvider("claude", _ => Task.FromResult(ClaudeCodeUsage.NotSignedIn));

        // A per-tick (null) floor would poll the remote endpoint every RefreshSeconds; the primary
        // account reads a remote endpoint just like the web path, so it keeps the 5-minute floor.
        Assert.Equal(TimeSpan.FromMinutes(5), provider.MinRefreshInterval);
    }

    [Fact]
    public void A_local_login_account_with_a_one_minute_setting_holds_no_five_minute_floor()
    {
        var settings = new AppSettings { RemoteRefreshMinutes = 1 };
        var provider = new ClaudeProvider("claude", _ => Task.FromResult(ClaudeCodeUsage.NotSignedIn), settings);

        // The local-login read pays only one plain request, unlike a web session's page load, so it
        // may follow the settings value all the way down instead of holding the web path's floor.
        Assert.Equal(TimeSpan.FromMinutes(1), provider.MinRefreshInterval);
    }

    [Fact]
    public void A_web_session_account_with_a_one_minute_setting_still_holds_the_five_minute_floor()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var settings = new AppSettings { RemoteRefreshMinutes = 1 };
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => throw new InvalidOperationException("must not be called"));
        var provider = new ClaudeProvider(directory, () => Now, webSource, settings, _ => { });

        Assert.Equal(TimeSpan.FromMinutes(5), provider.MinRefreshInterval);
    }

    [Fact]
    public void A_ten_minute_setting_matches_for_local_login_and_web_session_alike()
    {
        var settings = new AppSettings { RemoteRefreshMinutes = 10 };
        var localLoginProvider = new ClaudeProvider("claude", _ => Task.FromResult(ClaudeCodeUsage.NotSignedIn), settings);

        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => throw new InvalidOperationException("must not be called"));
        var webSessionProvider = new ClaudeProvider(directory, () => Now, webSource, settings, _ => { });

        Assert.Equal(TimeSpan.FromMinutes(10), localLoginProvider.MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), webSessionProvider.MinRefreshInterval);
    }

    // The primary account reads Claude Code's own sign-in: a browser sign-in would land in a profile
    // nothing reads, so it offers none, but it can still be disconnected and resumed.
    [Fact]
    public void The_primary_account_offers_sign_out_but_no_browser_sign_in()
    {
        var primary = new ClaudeProvider("claude", _ => Task.FromResult(ClaudeCodeUsage.NotSignedIn));
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => throw new InvalidOperationException("must not be called"));
        var further = new ClaudeProvider("claude#2", new AppSettings(), _ => { }, webSource);

        Assert.False(primary.SupportsInAppSignIn);
        Assert.True(primary.SupportsSignOut);
        Assert.True(further.SupportsInAppSignIn);
        Assert.True(further.SupportsSignOut);
    }

    [Fact]
    public async Task A_missing_local_credentials_file_names_claude_code_as_the_reason()
    {
        var provider = LocalLoginProviderWithoutTranscripts(_ => Task.FromResult(ClaudeCodeUsage.NotSignedIn));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Equal("State.NotSignedIn.ClaudeCode", snapshot.Error?.ReasonKey);
    }

    [Fact]
    public async Task The_local_login_and_account_label_reads_run_on_pool_threads()
    {
        var windows = new[] { new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, null, null) };
        var loginOnPool = new List<bool>();
        var labelOnPool = new List<bool>();
        var provider = new ClaudeProvider(
            projectsRoot: "" /* no transcript scan, so no earlier hop to the pool */, now: () => Now,
            localLogin: _ =>
            {
                loginOnPool.Add(Thread.CurrentThread.IsThreadPoolThread);
                return Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows));
            },
            readAccountLabel: () =>
            {
                labelOnPool.Add(Thread.CurrentThread.IsThreadPoolThread);
                return null;
            });

        // A dedicated thread stands in for the UI thread the scheduler awaits on.
        var snapshot = await Task.Factory.StartNew(
            () => provider.FetchAsync(CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal([true], loginOnPool);
        Assert.Equal([true], labelOnPool);
    }

    [Fact]
    public async Task A_signed_in_local_login_read_reports_web_login_windows_as_a_local_login_source()
    {
        var windows = new[] { new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, null, null) };
        var provider = new ClaudeProvider(
            projectsRoot: Path.Combine(FixturesDirectory, "does-not-exist"), now: () => Now,
            localLogin: _ => Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows)));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.Equal(42, snapshot.Windows.Single().UsedPercent);
    }
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T10:00:00Z");
    private static readonly WebSessionDescriptor Descriptor = new(
        ProviderId: "claude", BaseUrl: "https://claude.ai/",
        SignInUrl: "https://claude.ai/login", AllowedHosts: ["claude.ai"], ProfileFolderName: "claude");

    [Fact]
    public async Task FetchAsync_reports_limit_reached_with_a_full_bar_and_the_real_reset()
    {
        var provider = ProviderOverFile("claude-429.jsonl");

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.Single(snapshot.Windows);
        Assert.Equal(100, snapshot.Windows[0].UsedPercent);
        Assert.Equal(WindowKind.FiveHour, snapshot.Windows[0].Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T13:00:00Z"), snapshot.Windows[0].ResetsAt);
    }

    [Fact]
    public async Task FetchAsync_puts_the_session_token_sum_on_the_built_window()
    {
        var provider = ProviderOverFile("claude-429-with-tokens.jsonl");

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(10 + 20 + 30 + 40, snapshot.Windows.Single().Tokens?.TotalTokens);
    }

    [Fact]
    public async Task FetchAsync_reports_no_local_data_without_a_recent_rejection()
    {
        var provider = ProviderOverFile("claude-no-limits.jsonl");

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_reports_no_local_data_when_the_projects_directory_is_missing()
    {
        var provider = new ClaudeProvider(Path.Combine(FixturesDirectory, "does-not-exist"), () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
    }

    [Fact]
    public async Task A_local_rejection_wins_over_the_web_source_even_when_both_are_available()
    {
        var directory = SeedDirectory("claude-429.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => throw new InvalidOperationException("must not be called"));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
    }

    [Fact]
    public async Task With_no_local_hit_the_web_source_windows_are_used()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult(
            """{"status":"ok","path":"/api/organizations/abc/usage","body":"{\"five_hour\":{\"utilization\":55,\"resets_at\":1788624000}}"}"""));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Single(snapshot.Windows);
        Assert.Equal(55, snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task A_not_signed_in_web_source_produces_the_not_signed_in_status()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult("""{"status":"not_signed_in"}"""));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task An_extra_account_without_a_web_session_names_the_browser_as_the_reason()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult("""{"status":"not_signed_in"}"""));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Equal("State.NotSignedIn.WebSession", snapshot.Error?.ReasonKey);
    }

    [Fact]
    public async Task An_expired_local_token_names_the_expired_key_not_the_generic_one()
    {
        var provider = LocalLoginProviderWithoutTranscripts(
            _ => Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.TokenExpired)));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Equal("State.NotSignedIn.ClaudeExpired", snapshot.Error?.ReasonKey);
        Assert.Equal("token_expired", snapshot.SkipReasonWord);
    }

    private sealed class ExpiringLogin
    {
        public ClaudeCodeUsage Next = new(LocalLoginOutcome.Ok,
        [
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300),
            new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, Now.AddDays(3), 10080),
        ]);

        public Task<ClaudeCodeUsage> Read(CancellationToken _) => Task.FromResult(Next);

        public void Expire() => Next = new(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.TokenExpired);
    }

    private static ClaudeProvider LocalLoginProviderAt(Func<DateTimeOffset> now, ExpiringLogin login) => new(
        projectsRoot: Path.Combine(FixturesDirectory, "does-not-exist"), now: now, webSource: null,
        settings: null, saveSettings: null, localLogin: login.Read);

    [Fact]
    public async Task An_expired_token_after_a_good_read_keeps_the_last_numbers_with_their_age()
    {
        var clock = Now;
        var login = new ExpiringLogin();
        var provider = LocalLoginProviderAt(() => clock, login);
        await provider.FetchAsync(CancellationToken.None);

        login.Expire();
        clock = Now.AddMinutes(30);
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Null(snapshot.Error);
        Assert.Equal(new double[] { 42, 63 }, snapshot.Windows.Select(window => window.UsedPercent));
        Assert.Equal(Now, snapshot.DataTimestamp);
        Assert.Equal(Now.AddMinutes(30), snapshot.FetchedAt);
        Assert.Equal("token_expired", snapshot.SkipReasonWord);
        Assert.True(snapshot.HeldOver);
    }

    [Fact]
    public async Task A_held_window_that_reset_meanwhile_starts_over_at_zero()
    {
        var clock = Now;
        var login = new ExpiringLogin();
        var provider = LocalLoginProviderAt(() => clock, login);
        await provider.FetchAsync(CancellationToken.None);

        login.Expire();
        clock = Now.AddHours(3);
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var fiveHour = snapshot.Windows.Single(window => window.Kind == WindowKind.FiveHour);
        Assert.Equal(0, fiveHour.UsedPercent);
        Assert.Null(fiveHour.ResetsAt);
        Assert.Equal(63, snapshot.Windows.Single(window => window.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task Numbers_held_past_the_stale_limit_turn_stale()
    {
        var clock = Now;
        var login = new ExpiringLogin();
        var provider = LocalLoginProviderAt(() => clock, login);
        await provider.FetchAsync(CancellationToken.None);

        login.Expire();
        clock = Now + ProviderFreshness.StaleAfter + TimeSpan.FromMinutes(1);
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Stale, snapshot.Status);
        Assert.NotEmpty(snapshot.Windows);
    }

    [Fact]
    public async Task A_sign_in_that_is_really_gone_drops_the_held_numbers()
    {
        var clock = Now;
        var login = new ExpiringLogin();
        var provider = LocalLoginProviderAt(() => clock, login);
        await provider.FetchAsync(CancellationToken.None);

        login.Next = ClaudeCodeUsage.NotSignedIn;
        await provider.FetchAsync(CancellationToken.None);
        login.Expire();
        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Equal("State.NotSignedIn.ClaudeExpired", snapshot.Error?.ReasonKey);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_non_ok_response_from_the_usage_endpoint_reports_its_own_reason_word()
    {
        var provider = LocalLoginProviderWithoutTranscripts(
            _ => Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse)));

        AiUsage.Services.NetworkStatus.Probe = () => true;
        try
        {
            var snapshot = await provider.FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Failed, snapshot.Status);
            Assert.Equal("non_ok_response", snapshot.SkipReasonWord);
            Assert.DoesNotContain("sk-ant", snapshot.ToString());
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }

    [Fact]
    public async Task A_failed_web_source_falls_back_to_the_honest_no_local_data_snapshot()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult("""{"status":"failed"}"""));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        AiUsage.Services.NetworkStatus.Probe = () => true;
        try
        {
            var snapshot = await provider.FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
            Assert.NotEmpty(snapshot.Diagnostics!);
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }

    [Fact]
    public async Task A_failed_web_source_reports_offline_instead_of_no_local_data_when_the_machine_has_no_network()
    {
        var directory = SeedDirectory("claude-no-limits.jsonl");
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult("""{"status":"failed"}"""));
        var provider = new ClaudeProvider(directory, () => Now, webSource, new AppSettings(), _ => { });

        AiUsage.Services.NetworkStatus.Probe = () => false;
        try
        {
            var snapshot = await provider.FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Failed, snapshot.Status);
            Assert.Equal("Status_Offline_Reason", snapshot.Error?.ReasonKey);
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }

    [Fact]
    public async Task A_signed_out_local_login_still_falls_through_to_a_usable_web_session()
    {
        var webSource = new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult(
            """{"status":"ok","path":"/api/organizations/abc/usage","body":"{\"five_hour\":{\"utilization\":30,\"resets_at\":1788624000}}"}"""));
        var provider = new ClaudeProvider(
            projectsRoot: Path.Combine(FixturesDirectory, "does-not-exist"),
            now: () => Now,
            webSource: webSource,
            settings: new AppSettings(),
            saveSettings: _ => { },
            localLogin: _ => Task.FromResult(ClaudeCodeUsage.NotSignedIn));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Equal(30, snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_marks_the_snapshot_as_waiting_for_a_recent_finished_reply()
    {
        var provider = ProviderOverFile("claude-waiting.jsonl");

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.True(snapshot.IsWaitingForUser);
    }

    [Fact]
    public async Task FetchAsync_reports_not_waiting_for_a_malformed_session_file_without_throwing()
    {
        var provider = ProviderOverFile("claude-corrupt.jsonl");

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.False(snapshot.IsWaitingForUser);
    }

    // The tile's "tokens this session" line: the five-hour window of the local sign-in carries the
    // token sum of the newest Claude Code session file, same meaning as the Codex tile's figure.

    private static string UsageLine(DateTimeOffset at, long input, long output, string id) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + at.ToString("o") + "\",\"requestId\":\"req_" + id + "\",\"message\":{\"id\":\"msg_" + id +
        "\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}}";

    private string SeedSession(params string[] lines)
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-claude-provider-tokens");
        _tempDirectories.Add(directory);
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), string.Join("\n", lines) + "\n");
        return directory;
    }

    private static Func<CancellationToken, Task<ClaudeCodeUsage>> SignedInLocalLogin() =>
        _ => Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.Ok,
        [
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300),
            new UsageWindow("Window_Weekly", WindowKind.Weekly, 10, Now.AddDays(3), 10080),
        ]));

    [Fact]
    public async Task A_signed_in_local_read_puts_the_session_token_sum_on_the_five_hour_window()
    {
        var directory = SeedSession(
            UsageLine(Now.AddMinutes(-30), input: 100, output: 50, id: "1"),
            UsageLine(Now.AddMinutes(-10), input: 20, output: 5, id: "2"));
        var provider = new ClaudeProvider(directory, () => Now, localLogin: SignedInLocalLogin());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.Equal(175, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).Tokens?.TotalTokens);
        Assert.Null(snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).Tokens);

        var row = new AiUsage.ViewModels.UsageRowViewModel(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour), Now);
        Assert.Equal(175, row.TokenCount);
        Assert.Contains("175", row.InlineTokensText);
    }

    [Fact]
    public async Task A_session_that_went_quiet_before_the_current_window_shows_no_token_count()
    {
        var directory = SeedSession(UsageLine(Now.AddHours(-9), input: 100, output: 50, id: "1"));
        var provider = new ClaudeProvider(directory, () => Now, localLogin: SignedInLocalLogin());

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).Tokens);
    }

    [Fact]
    public async Task A_web_session_read_never_borrows_the_local_files_token_count()
    {
        var directory = SeedSession(UsageLine(Now.AddMinutes(-10), input: 100, output: 50, id: "1"));
        var envelope = "{\"status\":\"ok\",\"path\":\"/api/organizations/" + OrgId + "/usage\",\"body\":\"{\\\"five_hour\\\":{\\\"utilization\\\":42,\\\"resets_at\\\":1788624000}}\"}";
        var settings = new AppSettings();
        var provider = new ClaudeProvider(
            directory, () => Now, new WebUsageSource(Descriptor, ClaudeEndpoint, (_, _) => Task.FromResult(envelope)), settings, _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.All(snapshot.Windows, window => Assert.Null(window.Tokens));
    }

    [Fact]
    public async Task A_rotated_session_file_keeps_the_limit_found_in_it()
    {
        var directory = SeedDirectory("claude-429-with-tokens.jsonl");
        var file = Path.Combine(directory, "session.jsonl");
        // Prime the scan cache, then hold the file exclusively: the cached scan still answers, the
        // token sum cannot open the file any more.
        Assert.NotNull(AiUsage.Providers.Parsing.ClaudeLocalLimitReader.FindActiveLimit(directory, Now));
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var provider = new ClaudeProvider(directory, () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.Equal(100, snapshot.Windows.Single().UsedPercent);
        Assert.Null(snapshot.Windows.Single().Tokens);
    }

    [Fact]
    public async Task A_limit_in_the_second_of_two_projects_folders_is_found()
    {
        var empty = SeedDirectory("claude-no-limits.jsonl");
        var limited = SeedDirectory("claude-429.jsonl");
        var provider = new ClaudeProvider([empty, limited], () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T13:00:00Z"), snapshot.Windows.Single().ResetsAt);
        Assert.Equal(2, new ClaudeProvider([empty, limited], () => Now).ReadLocations.Count - 1);
    }

    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";

    private ClaudeProvider ProviderOverFile(string fixtureName) => new(SeedDirectory(fixtureName), () => Now);

    private string SeedDirectory(string fixtureName)
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-claude-provider");
        _tempDirectories.Add(directory);
        File.Copy(Path.Combine(FixturesDirectory, fixtureName), Path.Combine(directory, "session.jsonl"));
        return directory;
    }

    [Fact]
    public void The_default_projects_folder_is_shown_as_the_home_relative_path()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var provider = new ClaudeProvider(Path.Combine(profile, ".claude", "projects"), now: null);

        Assert.Equal("~/.claude/projects", provider.ReadLocations[0]);
    }

    [Fact]
    public void A_projects_folder_under_a_fake_home_is_shown_with_the_tilde()
    {
        Assert.Equal(
            "~/work/claude-cfg/projects",
            ClaudeProvider.DisplayPath(@"D:\Home\Pat\work\claude-cfg\projects", @"D:\Home\Pat"));
        Assert.Equal(
            "~/.claude/projects",
            ClaudeProvider.DisplayPath(@"d:\home\pat\.claude\projects", @"D:\Home\Pat\"));
    }

    [Fact]
    public void A_projects_folder_outside_the_home_never_names_the_user()
    {
        var shown = ClaudeProvider.DisplayPath(@"D:\Shared\Pat\claude\projects", @"D:\Home\Pat");

        Assert.DoesNotContain("Pat", shown);
        Assert.StartsWith(@"D:\Shared\", shown);
    }

    [Fact]
    public void A_folder_that_only_shares_the_home_prefix_is_not_home_relative()
    {
        Assert.DoesNotContain("~", ClaudeProvider.DisplayPath(@"D:\Home\PatTwo\.claude\projects", @"D:\Home\Pat"));
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
