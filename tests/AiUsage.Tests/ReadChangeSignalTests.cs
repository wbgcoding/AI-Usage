using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Tests;

/// <summary>
/// The generic change checks every provider's snapshot passes before it is shown (a moved address,
/// a success without a window, an answer nothing could be read from), and the providers' own marks
/// that feed the last one. The no-fire cases matter as much: a network failure, a timeout, a rate
/// limit, a signed-out session or a held fetch must never read as a change in the provider's answer.
/// </summary>
public class ReadChangeSignalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");

    private static readonly UsageWindow Window = new("Window_FiveHour", WindowKind.FiveHour, 10, null, 300);

    private static ProviderSnapshot Snapshot(
        ProviderStatus status, IReadOnlyList<UsageWindow>? windows = null, ProviderError? error = null, bool unreadable = false) =>
        new("example", windows ?? [], null, SourceKind.None, Now, null, status, error, UnreadableAnswer: unreadable);

    private static IReadOnlyList<string> Report(ProviderSnapshot snapshot)
    {
        using var capture = ReadChangeLog.BeginCapture();
        RefreshScheduler.ReportReadChanges("Example", snapshot);
        return capture.Lines;
    }

    [Fact]
    public void A_moved_endpoint_is_reported_with_its_status()
    {
        var line = Assert.Single(Report(Snapshot(
            ProviderStatus.Failed, error: new ProviderError("Status_Failed_Reason", Kind: FailureKind.EndpointMoved, HttpStatus: 404))));

        Assert.StartsWith("CHANGE DETECTED (Example usage read needs adapting): ", line, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_success_without_any_window_is_reported() =>
        Assert.Single(Report(Snapshot(ProviderStatus.Ok)));

    [Fact]
    public void An_answer_nothing_could_be_read_from_is_reported() =>
        Assert.Contains("nothing in it could be read", Assert.Single(Report(Snapshot(ProviderStatus.Failed, unreadable: true))), StringComparison.Ordinal);

    [Fact]
    public void The_unreadable_mark_and_an_empty_success_together_give_one_line() =>
        Assert.Single(Report(Snapshot(ProviderStatus.Ok, unreadable: true)));

    [Theory]
    [InlineData(FailureKind.Timeout)]
    [InlineData(FailureKind.Network)]
    [InlineData(FailureKind.RateLimited)]
    [InlineData(FailureKind.ServerError)]
    [InlineData(FailureKind.Refused)]
    [InlineData(FailureKind.Other)]
    public void A_failure_of_any_other_kind_is_no_change(FailureKind kind) =>
        Assert.Empty(Report(Snapshot(ProviderStatus.Failed, error: new ProviderError("Status_Failed_Reason", Kind: kind, HttpStatus: 500))));

    [Fact]
    public void A_failure_without_a_reason_is_no_change() =>
        Assert.Empty(Report(Snapshot(ProviderStatus.Failed)));

    [Theory]
    [InlineData(ProviderStatus.NotSignedIn)]
    [InlineData(ProviderStatus.NoLocalData)]
    [InlineData(ProviderStatus.Blocked)]
    [InlineData(ProviderStatus.SourceUnavailable)]
    [InlineData(ProviderStatus.RuntimeMissing)]
    [InlineData(ProviderStatus.Stale)]
    public void An_empty_snapshot_of_a_routine_state_is_no_change(ProviderStatus status) =>
        Assert.Empty(Report(Snapshot(status)));

    [Fact]
    public void A_success_with_a_window_is_no_change() =>
        Assert.Empty(Report(Snapshot(ProviderStatus.Ok, [Window])));

    [Fact]
    public void A_provider_that_named_the_change_itself_is_not_told_twice()
    {
        using var capture = ReadChangeLog.BeginCapture();

        ReadChangeLog.Report("Example", "new model 'x-1' found.");
        RefreshScheduler.ReportReadChanges("Example", Snapshot(ProviderStatus.Failed, unreadable: true));

        Assert.Single(capture.Lines);
    }

    [Fact]
    public async Task The_scheduler_reports_a_changed_answer_before_it_publishes_the_snapshot()
    {
        var provider = new StubProvider(Snapshot(ProviderStatus.Failed, unreadable: true));
        var scheduler = new RefreshScheduler([provider], TimeSpan.FromSeconds(60), TimeProvider.System);
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;
        using var capture = ReadChangeLog.BeginCapture();

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        Assert.Contains("(Stub usage read needs adapting)", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scheduler_stays_silent_for_a_timeout()
    {
        var provider = new StubProvider(Snapshot(ProviderStatus.Failed, error: new ProviderError("Status_Failed_Reason", Kind: FailureKind.Timeout)));
        var scheduler = new RefreshScheduler(
            [provider], TimeSpan.FromSeconds(60), TimeProvider.System, retryDelay: TimeSpan.Zero, logInfo: _ => { });
        using var capture = ReadChangeLog.BeginCapture();

        await Task.WhenAll(scheduler.Tick());

        Assert.Empty(capture.Lines);
    }

    // --- The marks the providers set ------------------------------------------------------------

    private static string NoProjects() => Path.Combine(AppContext.BaseDirectory, "no-such-projects");

    private static readonly WebSessionDescriptor ClaudeDescriptor = new(
        ProviderId: "claude", BaseUrl: "https://claude.ai/", SignInUrl: "https://claude.ai/login",
        AllowedHosts: ["claude.ai"], ProfileFolderName: "claude");

    [Fact]
    public async Task Cursor_marks_a_signed_in_answer_it_cannot_read()
    {
        NetworkStatus.Probe = () => true;
        var provider = new CursorProvider(
            _ => Task.FromResult(new WebUsageResult(WebUsageOutcome.Failed, [], SessionSignedIn: true)), () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.True(snapshot.UnreadableAnswer);
    }

    [Fact]
    public async Task Cursor_marks_an_ok_answer_without_a_window()
    {
        var provider = new CursorProvider(_ => Task.FromResult(new WebUsageResult(WebUsageOutcome.Ok, [])), () => Now);

        Assert.True((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Cursor_does_not_mark_a_dead_connection_a_sign_out_or_a_block()
    {
        NetworkStatus.Probe = () => true;
        foreach (var result in new[] { WebUsageResult.Failed, WebUsageResult.NotSignedIn, WebUsageResult.Blocked })
        {
            var provider = new CursorProvider(_ => Task.FromResult(result), () => Now);

            Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
        }
    }

    [Fact]
    public async Task Cursor_does_not_mark_anything_while_offline()
    {
        NetworkStatus.Probe = () => false;
        var provider = new CursorProvider(
            _ => Task.FromResult(new WebUsageResult(WebUsageOutcome.Failed, [], SessionSignedIn: true)), () => Now);

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Gemini_marks_a_local_answer_without_any_quota()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, """{"groups":[]}""", "Pro", ModelQuotaJson: """{"buckets":[]}""");
        var provider = new GeminiProvider(_ => Task.FromResult(usage), () => Now, readAccountLabel: () => null);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.True(snapshot.UnreadableAnswer);
    }

    [Theory]
    [InlineData(FailureKind.Timeout, null)]
    [InlineData(FailureKind.RateLimited, 429)]
    [InlineData(FailureKind.Network, null)]
    public async Task Gemini_does_not_mark_a_failed_request(FailureKind kind, int? status)
    {
        NetworkStatus.Probe = () => true;
        var usage = new AntigravityUsage(LocalLoginOutcome.Failed, "", null, kind, status);
        var provider = new GeminiProvider(_ => Task.FromResult(usage), () => Now, readAccountLabel: () => null);

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Copilot_marks_an_answer_without_the_quota_object()
    {
        var fetch = new CopilotFetch(GitHubCliOutcome.Ok, """{"login":"octocat","quotas":{}}""");
        var provider = new CopilotProvider(_ => Task.FromResult(fetch), () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.True(snapshot.UnreadableAnswer);
    }

    [Fact]
    public async Task Copilot_does_not_mark_an_account_whose_quotas_are_all_unlimited()
    {
        var fetch = new CopilotFetch(
            GitHubCliOutcome.Ok, """{"login":"octocat","quota_snapshots":{"chat":{"unlimited":true,"percent_remaining":100}}}""");
        var provider = new CopilotProvider(_ => Task.FromResult(fetch), () => Now);

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Copilot_does_not_mark_a_failed_run_of_the_tool()
    {
        NetworkStatus.Probe = () => true;
        var provider = new CopilotProvider(_ => Task.FromResult(CopilotFetch.Failed), () => Now);

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Claude_marks_a_local_sign_in_answer_it_cannot_read()
    {
        NetworkStatus.Probe = () => true;
        var usage = new ClaudeCodeUsage(LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse, AnswerUnread: true);
        var provider = new ClaudeProvider(NoProjects(), () => Now, localLogin: _ => Task.FromResult(usage));

        Assert.True((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Claude_does_not_mark_a_timed_out_local_request()
    {
        NetworkStatus.Probe = () => true;
        var usage = new ClaudeCodeUsage(
            LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse, FailureKind: FailureKind.Timeout);
        var provider = new ClaudeProvider(NoProjects(), () => Now, localLogin: _ => Task.FromResult(usage));

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Claude_marks_a_signed_in_web_answer_it_cannot_read_even_without_another_source()
    {
        NetworkStatus.Probe = () => true;
        var webSource = new WebUsageSource(ClaudeDescriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(
            """{"status":"ok","path":"/api/organizations/abc/usage","body":"{\"surprise\":true}"}"""));
        var provider = new ClaudeProvider(NoProjects(), () => Now, webSource, new AppSettings(), _ => { });

        Assert.True((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public async Task Claude_does_not_mark_a_failed_web_script()
    {
        NetworkStatus.Probe = () => true;
        var webSource = new WebUsageSource(
            ClaudeDescriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult("""{"status":"failed"}"""));
        var provider = new ClaudeProvider(NoProjects(), () => Now, webSource, new AppSettings(), _ => { });

        Assert.False((await provider.FetchAsync(CancellationToken.None)).UnreadableAnswer);
    }

    [Fact]
    public void The_chooser_keeps_the_mark_of_a_source_that_lost()
    {
        var unreadable = Snapshot(ProviderStatus.Failed, unreadable: true);
        var good = Snapshot(ProviderStatus.Ok, [Window]) with { SourceKind = SourceKind.LocalLogin, DataTimestamp = Now };

        var picked = SnapshotChooser.Pick([unreadable, good]);

        Assert.Equal(ProviderStatus.Ok, picked.Status);
        Assert.True(picked.UnreadableAnswer);
    }

    private sealed class StubProvider(ProviderSnapshot snapshot) : IUsageProvider
    {
        public string Id => "stub";
        public string DisplayName => "Stub";
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(snapshot with { ProviderId = "stub" });
    }
}
