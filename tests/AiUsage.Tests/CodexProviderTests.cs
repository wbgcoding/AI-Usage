using System.Text.Json;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Web;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class CodexProviderTests : IDisposable
{
    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public async Task FetchAsync_reads_both_windows_and_the_plan_from_the_newest_file()
    {
        var provider = ProviderOverFile("codex-normal.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(2.0, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(88.0, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_reports_zero_percent_when_no_line_has_rate_limits()
    {
        var provider = ProviderOverFile("codex-no-ratelimits.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
        Assert.NotNull(snapshot.DataTimestamp);
    }

    [Fact]
    public async Task FetchAsync_reports_zero_percent_for_an_empty_file()
    {
        var provider = ProviderOverFile("codex-empty.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
    }

    [Fact]
    public async Task FetchAsync_falls_back_to_the_second_to_last_line_when_the_last_one_is_truncated()
    {
        var provider = ProviderOverFile("codex-truncated.jsonl", now: DateTimeOffset.Parse("2026-09-01T09:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(5.0, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(10.0, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_marks_data_older_than_twelve_hours_as_stale()
    {
        var provider = ProviderOverFile("codex-old.jsonl", now: DateTimeOffset.Parse("2026-09-02T06:00:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Stale, snapshot.Status);
        Assert.NotEmpty(snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_reports_no_local_data_when_the_sessions_directory_is_missing()
    {
        var provider = new CodexProvider(
            Path.Combine(FixturesDirectory, "does-not-exist"), () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
    }

    [Fact]
    public async Task FetchAsync_reports_zero_percent_when_the_sessions_directory_exists_but_is_empty()
    {
        var directory = NewSessionsDirectory();

        var snapshot = await new CodexProvider(directory, () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"))
            .FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
        Assert.NotNull(snapshot.DataTimestamp);
    }

    [Fact]
    public async Task FetchAsync_no_local_data_result_carries_no_windows_or_timestamp_when_the_directory_is_missing()
    {
        var provider = new CodexProvider(
            Path.Combine(FixturesDirectory, "does-not-exist"), () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.DataTimestamp);
        Assert.Equal(SourceKind.None, snapshot.SourceKind);
    }

    [Fact]
    public async Task FetchAsync_looks_past_the_newest_files_when_those_hold_only_empty_windows()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        for (var index = 0; index < 5; index++)
            PlaceFixture(directory, "codex-null-windows.jsonl", $"rollout-empty-{index}.jsonl", now.AddMinutes(-index));
        PlaceFixture(directory, "codex-normal.jsonl", "rollout-real.jsonl", now.AddMinutes(-5));

        var snapshot = await new CodexProvider(directory, () => now).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(2.0, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(88.0, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_picks_the_newest_event_by_its_own_timestamp_not_by_file_write_time()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        // Written most recently, but its rate-limit event is the OLDER one (2026-09-01T00:00:00Z).
        PlaceFixture(directory, "codex-old.jsonl", "rollout-newer-write.jsonl", now);
        // Written earlier, but its rate-limit event is the NEWER one (2026-09-01T10:00:00Z).
        PlaceFixture(directory, "codex-normal.jsonl", "rollout-older-write.jsonl", now.AddMinutes(-5));

        var snapshot = await new CodexProvider(directory, () => now).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2.0, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(88.0, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_reports_zero_percent_when_every_file_is_older_than_a_week()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        PlaceFixture(directory, "codex-normal.jsonl", "rollout-ancient.jsonl", now.AddDays(-8));

        var snapshot = await new CodexProvider(directory, () => now).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
    }

    [Fact]
    public async Task FetchAsync_stops_a_file_at_the_first_reverse_line_older_than_a_week()
    {
        var now = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var directory = NewSessionsDirectory();
        var resetsAt = UnixSeconds(now.AddHours(3));
        // A usable rate-limit line sits before the final line, and the final line (read first) is 8
        // days old: the scan stops there and never reaches the older percentage.
        File.WriteAllLines(Path.Combine(directory, "rollout-test.jsonl"),
        [
            RateLimitLine(now.AddDays(-9), primaryPercent: 15, primaryResetsAtUnix: resetsAt),
            """{"timestamp":"2026-09-02T09:00:00Z","type":"event_msg","payload":{"type":"other_event"}}""",
        ]);

        var snapshot = await new CodexProvider(directory, () => now).FetchAsync(CancellationToken.None);

        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
    }

    [Fact]
    public async Task FetchAsync_still_reads_a_recent_rate_limit_line_behind_a_recent_plain_line()
    {
        var now = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var directory = NewSessionsDirectory();
        var resetsAt = UnixSeconds(now.AddHours(3));
        File.WriteAllLines(Path.Combine(directory, "rollout-test.jsonl"),
        [
            RateLimitLine(now.AddDays(-1), primaryPercent: 15, primaryResetsAtUnix: resetsAt),
            """{"timestamp":"2026-09-10T09:00:00Z","type":"event_msg","payload":{"type":"other_event"}}""",
        ]);

        var snapshot = await new CodexProvider(directory, () => now).FetchAsync(CancellationToken.None);

        Assert.Equal(15, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_reports_a_window_whose_reset_already_passed_as_empty_and_keeps_the_snapshot_fresh()
    {
        var provider = ProviderOverFile("codex-reset-mixed.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:05:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        var fiveHour = snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Null(fiveHour.ResetsAt);
        Assert.Equal(0, fiveHour.UsedPercent);
        Assert.Equal(25, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_keeps_a_reset_instant_that_is_still_in_the_future()
    {
        var provider = ProviderOverFile("codex-reset-mixed.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:05:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.NotNull(snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).ResetsAt);
    }

    [Fact]
    public async Task FetchAsync_drops_both_reset_instants_when_both_windows_have_already_reset()
    {
        var provider = ProviderOverFile("codex-reset-both-past.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:05:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.All(snapshot.Windows, w => Assert.Null(w.ResetsAt));
        Assert.All(snapshot.Windows, w => Assert.Equal(0, w.UsedPercent));
    }

    [Fact]
    public async Task FetchAsync_still_carries_a_real_token_count_once_routed_through_the_shared_chooser()
    {
        var provider = ProviderOverFile("codex-with-tokens.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.NotNull(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).Tokens);
    }

    [Fact]
    public async Task FetchAsync_marks_the_snapshot_as_waiting_for_a_recent_finished_reply()
    {
        var provider = ProviderOverFile("codex-waiting.jsonl", now: DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.True(snapshot.IsWaitingForUser);
    }

    [Fact]
    public async Task FetchAsync_reports_not_waiting_for_a_truncated_session_file_without_throwing()
    {
        var provider = ProviderOverFile("codex-truncated.jsonl", now: DateTimeOffset.Parse("2026-09-01T09:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.False(snapshot.IsWaitingForUser);
    }

    [Fact]
    public async Task FetchAsync_passes_its_own_configured_attention_max_age_through_rather_than_the_default()
    {
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-waiting.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        // Five minutes after the newest assistant turn (09:58:00Z) - inside the default two-hour
        // max age, but outside a two-minute one, so only a provider that actually forwards its own
        // configured value (rather than silently falling back to the default) reports not waiting.
        var now = DateTimeOffset.Parse("2026-09-01T10:03:00Z");

        var atDefault = new CodexProvider(directory, () => now);
        var atTwoMinutes = new CodexProvider(directory, () => now, attentionMaxAge: () => TimeSpan.FromMinutes(2));

        Assert.True((await atDefault.FetchAsync(CancellationToken.None)).IsWaitingForUser);
        Assert.False((await atTwoMinutes.FetchAsync(CancellationToken.None)).IsWaitingForUser);
    }

    [Fact]
    public async Task FetchAsync_picks_up_a_changed_attention_max_age_without_rebuilding_the_provider()
    {
        // The setting is a delegate read on every fetch, not a value captured once at construction -
        // changing the settings-window dropdown must take effect on the provider's very next read,
        // never only after the app restarts and rebuilds it.
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-waiting.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var now = DateTimeOffset.Parse("2026-09-01T10:03:00Z");
        var maxAge = TimeSpan.FromMinutes(2);

        var provider = new CodexProvider(directory, () => now, attentionMaxAge: () => maxAge);
        Assert.False((await provider.FetchAsync(CancellationToken.None)).IsWaitingForUser);

        maxAge = TimeSpan.FromHours(2);
        Assert.True((await provider.FetchAsync(CancellationToken.None)).IsWaitingForUser);
    }

    /// <summary>
    /// Puts a single fixture file into its own temp directory: CodexProvider scans a whole
    /// directory tree, and the real fixture folder holds every other fixture too.
    /// The internal constructor is visible here via InternalsVisibleTo (AiUsage.csproj).
    /// </summary>
    private CodexProvider ProviderOverFile(string fixtureName, DateTimeOffset now)
    {
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, fixtureName), Path.Combine(directory, "rollout-test.jsonl"));
        return new CodexProvider(directory, () => now);
    }

    [Fact]
    public async Task The_web_session_supplies_the_numbers_when_no_session_file_has_any()
    {
        var provider = ProviderWithWebSession(NewSessionsDirectory(), _ => OkEnvelope, out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(12.5, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.True(snapshot.WebSessionSignedIn);
    }

    /// <summary>The local files answer perfectly well while the browser session is signed out - the
    /// tile has to keep showing them and still be able to offer the sign-in that makes them live.</summary>
    [Fact]
    public async Task A_signed_out_web_session_leaves_the_local_numbers_standing_and_reports_itself()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-normal.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var provider = ProviderWithWebSession(directory, _ => """{"status":"not_signed_in"}""", out _, () => now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.False(snapshot.WebSessionSignedIn);
    }

    /// <summary>The address belongs to the signed-in account, not to the source that won the
    /// numbers: a web answer without usable windows still names the account on the local snapshot.</summary>
    [Fact]
    public async Task The_web_account_address_survives_a_tick_the_local_files_win()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-normal.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var email = "user" + "@" + "example.com";
        var envelope = """{"status":"ok","path":"/backend-api/codex/usage","body":"{}","email":"__EMAIL__"}""".Replace("__EMAIL__", email);
        var provider = ProviderWithWebSession(directory, _ => envelope, out _, () => now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.Equal(email, snapshot.AccountLabel);
    }

    /// <summary>A signed-in session whose usage answer has a shape the parser does not know yet is
    /// still signed in: the sign-out button must not disappear together with the numbers.</summary>
    [Fact]
    public async Task A_signed_in_answer_the_parser_cannot_read_still_counts_as_signed_in()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-normal.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var envelope = """{"status":"ok","path":"/backend-api/codex/usage","body":"{\"unknown\":1}"}""";
        var provider = ProviderWithWebSession(directory, _ => envelope, out _, () => now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
        Assert.True(snapshot.WebSessionSignedIn);
    }

    /// <summary>Before the first sign-in there is no session to read, and starting a browser process
    /// to find that out would cost a whole WebView2 host and a request to the provider's site for a
    /// user who may never want that route. The tile still has to learn it is signed out, or it could
    /// never offer the sign-in.</summary>
    [Fact]
    public async Task No_browser_session_is_started_before_the_first_sign_in()
    {
        var provider = ProviderWithWebSession(
            NewSessionsDirectory(), _ => OkEnvelope, out var scriptRuns, now: null, profileFolderExists: false);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Empty(scriptRuns);
        Assert.False(snapshot.WebSessionSignedIn);
        Assert.Equal(SourceKind.LocalFile, snapshot.SourceKind);
    }

    /// <summary>"Waiting for the user" can only be seen in the session files, so it has to survive a
    /// tick the web snapshot wins - otherwise the attention mark disappears for good once signed in.</summary>
    [Fact]
    public async Task The_attention_mark_survives_a_tick_the_web_snapshot_wins()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
        var directory = NewSessionsDirectory();
        File.Copy(Path.Combine(FixturesDirectory, "codex-waiting.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var provider = ProviderWithWebSession(directory, _ => OkEnvelope, out _, () => now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.True(snapshot.IsWaitingForUser);
    }

    /// <summary>One page load per five minutes, however often the tile ticks: the session files are
    /// free to re-read, the browser session is not.</summary>
    [Fact]
    public async Task The_web_session_is_read_once_per_interval_not_on_every_tick()
    {
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(NewSessionsDirectory(), _ => OkEnvelope, out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Single(scriptRuns);

        now = now.AddMinutes(5);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, scriptRuns.Count);
    }

    /// <summary>A sign-in inside the throttle interval used to get the cached "not signed in" answer
    /// back, which flipped the tile straight back to the sign-in button.</summary>
    [Fact]
    public async Task A_finished_sign_in_reads_the_web_again_inside_the_interval()
    {
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(NewSessionsDirectory(), _ => OkEnvelope, out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(1);
        provider.SignInCompleted();
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, scriptRuns.Count);

        now = now.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, scriptRuns.Count);
    }

    // A sign-in that finished while a web read was already running used to be cleared by that same
    // read, so the next tick got the cached answer again.
    [Fact]
    public async Task A_sign_in_finishing_during_a_web_read_still_counts_for_the_next_read()
    {
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        CodexProvider? provider = null;
        var signalOnFirstRead = true;
        provider = ProviderWithWebSession(NewSessionsDirectory(), _ =>
        {
            if (signalOnFirstRead)
            {
                signalOnFirstRead = false;
                provider!.SignInCompleted();
            }
            return OkEnvelope;
        }, out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        var runsAfterFirst = scriptRuns.Count;
        now = now.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);

        Assert.True(scriptRuns.Count > runsAfterFirst);
    }

    // The web answer is cached for five minutes; a window that resets inside that time must not keep
    // showing its old value from the cache.
    [Fact]
    public async Task A_cached_web_window_that_resets_reads_as_empty()
    {
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        const string resettingSoon = """
            {"status":"ok","path":"/backend-api/codex/usage","body":"{\"rate_limits\":{\"primary\":{\"used_percent\":90,\"window_minutes\":300,\"reset_after_seconds\":120},\"secondary\":{\"used_percent\":40,\"window_minutes\":10080,\"reset_after_seconds\":86400}}}"}
            """;
        var provider = ProviderWithWebSession(NewSessionsDirectory(), _ => resettingSoon, out _, () => now);

        var first = await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(3);
        var cached = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(90, first.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(0, cached.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(40, cached.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    // The cached answer is offered again under the new fetch time, so its age stays visible.
    [Fact]
    public async Task A_cached_web_answer_keeps_its_data_time_under_the_new_fetch_time()
    {
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(NewSessionsDirectory(), _ => OkEnvelope, out _, () => now);

        var first = await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(3);
        var cached = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(now, cached.FetchedAt);
        Assert.Equal(first.DataTimestamp, cached.DataTimestamp);
        Assert.True(cached.HeldOver);
        Assert.False(first.HeldOver);
    }

    // One discovered path plus a rate-limit body in the shape the session files use, as the hidden
    // session's script hands it back. Invented numbers, no account data.
    private const string OkEnvelope = """
        {"status":"ok","path":"/backend-api/codex/usage","body":"{\"rate_limits\":{\"primary\":{\"used_percent\":12.5,\"window_minutes\":300},\"secondary\":{\"used_percent\":64,\"window_minutes\":10080}}}"}
        """;

    /// <summary>A browser session is only ever read once one exists on disk, so every test above runs
    /// against a fake profile root with the provider's own folder already in it. The one test below
    /// leaves that folder out on purpose.</summary>
    private CodexProvider ProviderWithWebSession(
        string sessionsRoot, Func<string, string> answerScript, out List<string> scriptRuns,
        Func<DateTimeOffset>? now = null, bool profileFolderExists = true)
    {
        var profileRoot = TempDirectory("ai-usage-codex-webview");
        WebViewHost.SetRootOverride(profileRoot);
        if (profileFolderExists)
            Directory.CreateDirectory(Path.Combine(profileRoot, "codex"));

        var runs = new List<string>();
        scriptRuns = runs;
        var clock = now ?? (() => DateTimeOffset.Parse("2026-09-18T20:00:00Z"));
        var descriptor = new WebSessionDescriptor(
            ProviderId: "codex", BaseUrl: "https://chatgpt.com/codex/settings/usage",
            SignInUrl: "https://chatgpt.com/", AllowedHosts: ["chatgpt.com"], ProfileFolderName: "codex");
        var source = new WebUsageSource(descriptor, new CodexUsageEndpoint(clock), (script, _) =>
        {
            runs.Add(script);
            return Task.FromResult(answerScript(script));
        });

        return new CodexProvider(sessionsRoot, clock, source, new AppSettings(), _ => { });
    }

    [Fact]
    public async Task FetchAsync_reports_the_five_hour_window_as_full_once_a_later_line_shows_the_limit_was_actually_reached()
    {
        var provider = ProviderOverFile("codex-usage-limit-reached.jsonl", now: DateTimeOffset.Parse("2026-09-01T09:10:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(100, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        // The weekly window is untouched - a five-hour block says nothing about it.
        Assert.Equal(50, snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
        // The recorded reset time still stands - only the stale percentage was replaced.
        Assert.NotNull(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).ResetsAt);
    }

    [Fact]
    public async Task FetchAsync_still_reports_the_last_written_percentage_without_a_rejection_line()
    {
        var provider = ProviderOverFile("codex-96-percent-no-rejection.jsonl", now: DateTimeOffset.Parse("2026-09-01T09:10:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(96, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    // Reproduces the real spike: a session file's own conversation and tool-output content coincidentally
    // contains "429" or "usage limit" (an id, a hash, a discussion of this very rejection-detection code)
    // written AFTER the newest real rate-limit line - reading newest-first, the old whole-line substring
    // search reached that content before the real percent line and misread it as a genuine rejection,
    // reporting the five-hour window as 100 % even though nothing in the file ever actually said so.

    [Fact]
    public async Task FetchAsync_ignores_a_coincidental_429_inside_an_unrelated_completed_item()
    {
        var directory = NewSessionsDirectory();
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        File.WriteAllLines(Path.Combine(directory, "rollout-test.jsonl"),
        [
            RateLimitLine(DateTimeOffset.Parse("2026-09-01T09:00:00Z"), primaryPercent: 15, primaryResetsAtUnix: resetsAt),
            // An ordinary tool-call completion whose id happens to contain the digits "429".
            JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "item_completed", item = new { id = "item_ab429cd" } } }),
        ]);

        var snapshot = await new CodexProvider(directory, () => DateTimeOffset.Parse("2026-09-01T09:10:00Z")).FetchAsync(CancellationToken.None);

        Assert.Equal(15, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_ignores_an_unrelated_message_that_merely_mentions_a_usage_limit()
    {
        var directory = NewSessionsDirectory();
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        File.WriteAllLines(Path.Combine(directory, "rollout-test.jsonl"),
        [
            RateLimitLine(DateTimeOffset.Parse("2026-09-01T09:00:00Z"), primaryPercent: 15, primaryResetsAtUnix: resetsAt),
            // An assistant message discussing this exact feature, not an actual API error.
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new { type = "message", content = "This check reports a usage limit once the account is blocked." },
            }),
        ]);

        var snapshot = await new CodexProvider(directory, () => DateTimeOffset.Parse("2026-09-01T09:10:00Z")).FetchAsync(CancellationToken.None);

        Assert.Equal(15, snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    // The plausibility guard: a second, independent line of defence against exactly the same failure
    // shape, regardless of what produces it - a single local reading jumping 40+ points within the same
    // reset window is held back for one tick, then either confirmed or dropped by the next reading.

    [Fact]
    public async Task FetchAsync_holds_an_implausible_jump_for_one_tick_then_accepts_the_reading_that_contradicts_it()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 15, primaryResetsAtUnix: resetsAt);
        var first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, first.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = now.AddMinutes(5);
        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        var second = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, second.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = now.AddMinutes(5);
        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 16, primaryResetsAtUnix: resetsAt);
        var third = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(16, third.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_keeps_holding_a_jump_while_only_the_same_line_is_read_again()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 15, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        now = now.AddMinutes(5);
        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        // No new line: the same outlier read again must not count as its own confirmation.
        now = now.AddMinutes(5);
        var third = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, third.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_shows_a_held_jump_after_fifteen_minutes_without_a_newer_line()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T14:00:00Z"));
        var t0 = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var now = t0;
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 15, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        // The jump is first seen (and held) at t0.
        now = t0.AddMinutes(1);
        WriteTick(filePath, now.AddMinutes(-1), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        var heldAtStart = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, heldAtStart.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        // Same line read again: still hidden ten minutes later, shown once sixteen have passed.
        now = t0.AddMinutes(11);
        var tenMinutes = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, tenMinutes.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = t0.AddMinutes(17);
        var sixteenMinutes = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(100, sixteenMinutes.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_restarts_the_hold_timer_when_a_newer_line_contradicts_the_held_jump()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T14:00:00Z"));
        var t0 = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var now = t0;
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 15, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        now = t0.AddMinutes(1);
        WriteTick(filePath, now.AddMinutes(-1), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        // A newer line near the old value proves the jump was noise.
        now = t0.AddMinutes(10);
        WriteTick(filePath, now.AddMinutes(-1), primaryPercent: 16, primaryResetsAtUnix: resetsAt);
        var contradicted = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(16, contradicted.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        // A fresh jump starts its own fifteen minutes: the first one's age must not carry over.
        now = t0.AddMinutes(14);
        WriteTick(filePath, now.AddMinutes(-1), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        await provider.FetchAsync(CancellationToken.None);

        now = t0.AddMinutes(20);
        var stillHeld = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(16, stillHeld.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = t0.AddMinutes(30);
        var shown = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(100, shown.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_accepts_an_implausible_jump_once_the_next_reading_confirms_it()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 15, primaryResetsAtUnix: resetsAt);
        var first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, first.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = now.AddMinutes(5);
        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 100, primaryResetsAtUnix: resetsAt);
        var second = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(15, second.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        // Confirms the jump: close to the held-back reading, not to the old baseline.
        now = now.AddMinutes(5);
        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 98, primaryResetsAtUnix: resetsAt);
        var third = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(98, third.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    /// <summary>
    /// Stands in for a real, long-running VM check (the exclusive test VM was not available for this
    /// run): one session file grows over many ticks the way a real coding session does - the real
    /// percent climbing slowly - with a coincidental "429" or "usage limit" mention appended into
    /// ordinary tool-output or conversation content every few ticks, the same shape found in a real
    /// session file. Across every tick, the shown five-hour percentage must equal the genuine reading
    /// for that tick - nothing above it ever slipped through, confirmed or not.
    /// </summary>
    [Fact]
    public async Task FetchAsync_never_shows_a_value_above_the_genuine_reading_across_many_ticks_with_recurring_noise()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T15:00:00Z"));
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var provider = new CodexProvider(directory, () => now);
        var realPercents = new[] { 13, 14, 14, 15, 15, 16, 17, 17, 18, 19, 19, 20, 20, 21, 21 };

        using var writer = new StreamWriter(filePath, append: true);
        for (var tick = 0; tick < realPercents.Length; tick++)
        {
            now = now.AddMinutes(5);
            var eventTimestamp = now.AddSeconds(-30);
            writer.WriteLine(RateLimitLine(eventTimestamp, realPercents[tick], resetsAt));

            // Every third tick, the session keeps going with content that coincidentally contains the
            // exact substrings the old whole-line search misread as a rejection.
            if (tick % 3 == 2)
            {
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "event_msg",
                    payload = new { type = "item_completed", item = new { id = $"item_{tick}429end" } },
                }));
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "response_item",
                    payload = new { type = "message", content = "Discussing how this usage limit check behaves." },
                }));
            }

            writer.Flush();
            File.SetLastWriteTimeUtc(filePath, now.UtcDateTime);

            var snapshot = await provider.FetchAsync(CancellationToken.None);
            var shown = snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent;

            Assert.True(realPercents[tick] == shown,
                $"Tick {tick}: shown {shown} does not match the genuine reading {realPercents[tick]}.");
        }
    }

    private static string ErrorLine(DateTimeOffset timestamp, string message) =>
        JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new { type = "error", message },
        });

    [Theory]
    [InlineData("request id 4291", false)]
    [InlineData("trace 1429 failed", false)]
    [InlineData("unexpected status 429 Too Many Requests", true)]
    [InlineData("HTTP 429", true)]
    [InlineData("429 Too Many Requests", true)]
    [InlineData("You have reached your usage limit.", true)]
    public void A_rejection_is_recognised_by_the_status_wording_not_by_the_digits(string message, bool expected)
    {
        var line = ErrorLine(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), message);

        Assert.Equal(expected, CodexProvider.IsUsageLimitRejection(line));
    }

    [Fact]
    public async Task FetchAsync_shows_a_real_rejection_at_once_instead_of_holding_the_jump_from_30_to_100()
    {
        var directory = NewSessionsDirectory();
        var filePath = Path.Combine(directory, "rollout-test.jsonl");
        var resetsAt = UnixSeconds(DateTimeOffset.Parse("2026-09-01T14:00:00Z"));
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var provider = new CodexProvider(directory, () => now);

        WriteTick(filePath, now.AddMinutes(-2), primaryPercent: 30, primaryResetsAtUnix: resetsAt);
        var first = await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(30, first.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);

        now = now.AddMinutes(5);
        File.AppendAllText(filePath, ErrorLine(now.AddMinutes(-1), "unexpected status 429 Too Many Requests") + Environment.NewLine);
        File.SetLastWriteTimeUtc(filePath, now.UtcDateTime);
        var second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(100, second.Windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_attaches_no_token_figure_when_the_rollout_went_quiet_before_the_window()
    {
        var provider = ProviderOverFile("codex-with-tokens.jsonl", now: DateTimeOffset.Parse("2026-09-01T16:30:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).Tokens);
    }

    private static long UnixSeconds(DateTimeOffset instant) => instant.ToUnixTimeSeconds();

    private static void WriteTick(string filePath, DateTimeOffset eventTimestamp, double primaryPercent, long primaryResetsAtUnix)
    {
        File.WriteAllText(filePath, RateLimitLine(eventTimestamp, primaryPercent, primaryResetsAtUnix) + Environment.NewLine);
        File.SetLastWriteTimeUtc(filePath, eventTimestamp.UtcDateTime);
    }

    private static string RateLimitLine(DateTimeOffset timestamp, double primaryPercent, long primaryResetsAtUnix) =>
        JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    primary = new { used_percent = primaryPercent, window_minutes = 300, resets_at = primaryResetsAtUnix },
                    secondary = new { used_percent = 1.0, window_minutes = 10080, resets_at = 9_999_999_999L },
                },
            },
        });

    private string NewSessionsDirectory() => TempDirectory("ai-usage-codex");

    /// <summary>The scan order and the age cut-off both read the write time, so the test sets it.</summary>
    private static void PlaceFixture(string directory, string fixtureName, string fileName, DateTimeOffset writtenAt)
    {
        var path = Path.Combine(directory, fileName);
        File.Copy(Path.Combine(FixturesDirectory, fixtureName), path);
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory(string prefix)
    {
        var directory = TestPaths.CreateDisposableDirectory(prefix);
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
