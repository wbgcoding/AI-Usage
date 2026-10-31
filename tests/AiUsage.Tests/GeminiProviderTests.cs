using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Tests;

/// <summary>
/// Gemini reads through the Antigravity CLI's own sign-in, plus - once the app's own browser
/// session is signed in - the account's own usage endpoint on top of it. These tests drive the
/// provider with a canned <see cref="AntigravityUsage"/> instead of the real credential store and
/// network, so the mapping from each outcome to a snapshot is proven without a live login. The
/// response body shape is the real one measured from Antigravity's retrieveUserQuotaSummary, with
/// invented numbers.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class GeminiProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");

    private const string QuotaSummary = """
        {
          "groups": [
            {
              "displayName": "Gemini Models",
              "buckets": [
                { "window": "weekly", "resetTime": "2026-09-18T05:21:33Z", "remainingFraction": 0.9 },
                { "window": "5h", "resetTime": "2026-09-13T20:40:15Z", "remainingFraction": 1 }
              ]
            },
            {
              "displayName": "Claude and GPT models",
              "buckets": [
                { "window": "weekly", "resetTime": "2026-09-20T15:40:15Z", "remainingFraction": 0.5 },
                { "window": "5h", "resetTime": "2026-09-13T20:40:15Z", "remainingFraction": 1 }
              ]
            }
          ]
        }
        """;

    // Hermetic by default: never the real profile folder's account record.
    private static GeminiProvider Provider(AntigravityUsage usage, string? accountLabel = null) =>
        new((_) => Task.FromResult(usage), () => Now, readAccountLabel: () => accountLabel);

    [Fact]
    public async Task A_held_network_skips_the_sign_in_read_and_shows_no_data()
    {
        var reads = 0;
        var provider = new GeminiProvider(_ =>
        {
            reads++;
            return Task.FromResult(AntigravityUsage.NotSignedIn);
        }, () => Now, readAccountLabel: () => null);

        ProviderSnapshot snapshot;
        using (FetchContext.HoldNetwork())
            snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(0, reads);
        Assert.Equal(ProviderStatus.NoLocalData, snapshot.Status);
    }

    // Built by concatenation rather than as one literal - ShipCleanTests bans a real-looking email
    // address anywhere in the tracked source, fixtures included.
    private const string FakeEmail = "user" + "@" + "example.com";

    [Fact]
    public async Task The_local_read_runs_on_a_pool_thread_not_the_calling_thread()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        bool? onPool = null;
        var provider = new GeminiProvider(
            _ =>
            {
                onPool = Thread.CurrentThread.IsThreadPoolThread;
                return Task.FromResult(usage);
            },
            () => Now, readAccountLabel: () => null);

        // A dedicated thread stands in for the UI thread the scheduler awaits on.
        var snapshot = await Task.Factory.StartNew(
            () => provider.FetchAsync(CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.True(onPool);
    }

    [Fact]
    public async Task A_local_read_names_the_account_from_the_cli_record()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");

        var snapshot = await Provider(usage, FakeEmail).FetchAsync(CancellationToken.None);

        Assert.Equal(FakeEmail, snapshot.AccountLabel);
    }

    // Shape of the answers seen on 2026-10-17: the summary lists only the Claude/GPT group, the
    // per-model answer carries the Gemini models' shared five-hour quota.
    private const string SummaryWithoutGemini = """
        {"groups":[{"displayName":"Claude and GPT models","buckets":[
          {"window":"weekly","resetTime":"2026-10-22T21:14:07Z","remainingFraction":0.9997828},
          {"window":"5h","resetTime":"2026-10-17T15:17:16Z","remainingFraction":1}]}]}
        """;

    private const string ModelQuota = """
        {"buckets":[
          {"tokenType":"WTUS","modelId":"chat_20706","remainingFraction":1},
          {"resetTime":"2026-10-17T15:18:53Z","tokenType":"WTUS","modelId":"claude-opus-5-5-high","remainingFraction":0.5},
          {"resetTime":"2026-10-17T11:40:31Z","tokenType":"WTUS","modelId":"gemini-3.8-flash-tiered","remainingFraction":0.97771573},
          {"resetTime":"2026-10-17T11:40:31Z","tokenType":"WTUS","modelId":"gemini-2.5-pro","remainingFraction":0.97771573},
          {"tokenType":"WTUS","modelId":"tab_flash_lite_preview","remainingFraction":1}]}
        """;

    [Fact]
    public void A_summary_without_a_Gemini_group_never_shows_another_group_as_Gemini()
    {
        Assert.Empty(AiUsage.Providers.Parsing.GeminiUsageParser.Parse(SummaryWithoutGemini));
    }

    [Fact]
    public void The_per_model_answer_gives_the_Gemini_models_shared_five_hour_window()
    {
        var window = Assert.Single(AiUsage.Providers.Parsing.GeminiUsageParser.ParseModelQuota(ModelQuota));

        Assert.Equal(WindowKind.FiveHour, window.Kind);
        Assert.Equal(2.228427, window.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:40:31Z"), window.ResetsAt);
    }

    [Fact]
    public void An_unused_Gemini_quota_without_a_reset_still_shows_as_a_window()
    {
        var window = Assert.Single(AiUsage.Providers.Parsing.GeminiUsageParser.ParseModelQuota(
            """{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":1},{"modelId":"gemini-3-flash"}]}"""));

        Assert.Equal(0, window.UsedPercent);
        Assert.Null(window.ResetsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"buckets":[{"modelId":"claude-opus-5-5-high","resetTime":"2026-10-17T15:18:53Z","remainingFraction":0.5}]}""")]
    [InlineData("""{"buckets":[{"modelId":"gemini-2.5-pro","resetTime":"2026-10-17T11:40:31Z","remainingFraction":3}]}""")]
    public void A_per_model_answer_without_a_usable_Gemini_bucket_gives_no_window(string json)
    {
        Assert.Empty(AiUsage.Providers.Parsing.GeminiUsageParser.ParseModelQuota(json));
    }

    [Fact]
    public async Task Without_a_Gemini_group_the_tile_shows_the_per_model_quota()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, SummaryWithoutGemini, "Pro", ModelQuotaJson: ModelQuota);

        var snapshot = await Provider(usage, FakeEmail).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(2.228427, window.UsedPercent, precision: 3);
    }

    [Fact]
    public void The_answers_seen_when_the_read_was_adapted_need_no_change_note() =>
        Assert.Empty(AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(SummaryWithoutGemini, ModelQuota));

    [Fact]
    public async Task A_new_Gemini_model_is_reported_through_the_shared_change_log()
    {
        var quota = ModelQuota.Replace(
            """{"tokenType":"WTUS","modelId":"chat_20706","remainingFraction":1},""",
            """{"resetTime":"2026-10-17T11:40:31Z","modelId":"gemini-9-argon","remainingFraction":0.97771573},""");
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, SummaryWithoutGemini, "Pro", ModelQuotaJson: quota);
        using var capture = ReadChangeLog.BeginCapture();

        await Provider(usage, FakeEmail).FetchAsync(CancellationToken.None);

        var line = Assert.Single(capture.Lines);
        Assert.StartsWith("CHANGE DETECTED (Gemini usage read needs adapting): ", line, StringComparison.Ordinal);
        Assert.Contains("gemini-9-argon", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_Gemini_model_and_an_unknown_model_family_are_named()
    {
        var quota = ModelQuota.Replace(
            """{"tokenType":"WTUS","modelId":"chat_20706","remainingFraction":1},""",
            """{"resetTime":"2026-10-17T11:40:31Z","modelId":"gemini-4-argon","remainingFraction":0.97771573},"""
            + """{"resetTime":"2026-10-17T11:40:31Z","modelId":"argon-1","remainingFraction":0.5},""");

        var notes = AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(SummaryWithoutGemini, quota);

        Assert.Contains(notes, note => note.Contains("new Gemini model 'gemini-4-argon'", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("unknown model 'argon-1'", StringComparison.Ordinal));
    }

    [Fact]
    public void Gemini_models_with_different_quotas_are_noted()
    {
        var quota = ModelQuota.Replace("gemini-2.5-pro\",\"remainingFraction\":0.97771573", "gemini-2.5-pro\",\"remainingFraction\":0.5");

        var notes = AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(SummaryWithoutGemini, quota);

        Assert.Contains(notes, note => note.Contains("no longer share one quota", StringComparison.Ordinal));
    }

    [Fact]
    public void No_Gemini_quota_in_either_answer_names_the_groups_found()
    {
        var note = Assert.Single(AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(SummaryWithoutGemini, ""));

        Assert.Contains("Claude and GPT models", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_group_of_an_unknown_name_is_named()
    {
        var summary = SummaryWithoutGemini.Replace("Claude and GPT models", "Argon models");

        var notes = AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(summary, ModelQuota);

        Assert.Contains(notes, note => note.Contains("unknown group 'Argon models'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_window_in_the_Gemini_group_is_named() =>
        Assert.Contains(
            AiUsage.Providers.Parsing.GeminiUsageParser.ChangeNotes(
                """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"daily","remainingFraction":0.5},{"window":"5h","remainingFraction":0.5}]}]}""", ""),
            note => note.Contains("unknown window 'daily'", StringComparison.Ordinal));

    [Fact]
    public void An_exhausted_bucket_without_remainingFraction_reads_as_fully_used()
    {
        var windows = AiUsage.Providers.Parsing.GeminiUsageParser.Parse(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"2026-09-13T20:40:15Z"}]}]}""");

        var window = Assert.Single(windows);
        Assert.Equal(100, window.UsedPercent);
    }

    [Fact]
    public void A_bucket_with_a_fraction_outside_0_to_1_is_dropped_while_a_valid_bucket_still_shows()
    {
        var windows = AiUsage.Providers.Parsing.GeminiUsageParser.Parse(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"2026-09-13T20:40:15Z","remainingFraction":2},{"window":"weekly","resetTime":"2026-09-18T05:21:33Z","remainingFraction":0.9}]}]}""");

        var window = Assert.Single(windows);
        Assert.Equal(WindowKind.Weekly, window.Kind);
        Assert.Equal(10, window.UsedPercent, precision: 3);
    }

    [Fact]
    public async Task Not_signed_in_shows_the_sign_in_state_with_no_windows()
    {
        var snapshot = await Provider(AntigravityUsage.NotSignedIn).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Equal(SourceKind.None, snapshot.SourceKind);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_signed_in_read_fills_the_five_hour_and_weekly_windows_from_the_gemini_group()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");

        var snapshot = await Provider(usage).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.Equal("Google AI Pro", snapshot.PlanType);

        var fiveHour = snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        var weekly = snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(0, fiveHour.UsedPercent);
        Assert.Equal(10, weekly.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T05:21:33Z"), weekly.ResetsAt);
    }

    [Fact]
    public async Task A_signed_in_read_of_an_unrecognised_body_fails_rather_than_inventing_a_number()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, "{\"unexpected\":true}", PlanName: null);

        var snapshot = await Provider(usage).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_failed_read_reports_a_failure_not_a_zero()
    {
        var snapshot = await Provider(AntigravityUsage.Failed).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public void Gemini_signs_in_through_its_own_tool_outside_the_app()
    {
        var provider = Provider(AntigravityUsage.NotSignedIn);

        Assert.False(provider.SupportsInAppSignIn);
    }

    [Fact]
    public async Task FetchAsync_reports_offline_once_the_single_candidate_is_routed_through_the_shared_chooser()
    {
        AiUsage.Services.NetworkStatus.Probe = () => false;
        try
        {
            var snapshot = await Provider(AntigravityUsage.Failed).FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Failed, snapshot.Status);
            Assert.Equal("Status_Offline_Reason", snapshot.Error?.ReasonKey);
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }

    [Theory]
    [InlineData("Google AI Pro", "Pro")]
    [InlineData("Google AI Ultra", "Ultra")]
    [InlineData("Google AI Free", "Free")]
    public void ShortenPlanName_maps_a_known_tier_to_its_short_form(string longForm, string shortForm)
    {
        Assert.Equal(shortForm, AntigravityStateReader.ShortenPlanName(longForm));
    }

    [Fact]
    public void ShortenPlanName_leaves_an_unrecognised_name_untouched()
    {
        Assert.Equal("Some Future Tier", AntigravityStateReader.ShortenPlanName("Some Future Tier"));
    }

    // A discovered path plus a quota body in the shape the local sign-in uses too, as the hidden
    // session's script hands it back. Invented numbers, no account data.
    private const string NotSignedInEnvelope = """{"status":"not_signed_in"}""";

    private const string OkEnvelope = """
        {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.25,1,[[1792246090,0]]]],false]"}
        """;

    /// <summary>SnapshotChooser prefers more windows over fewer once both candidates are otherwise
    /// usable (see <see cref="Services.SnapshotChooser"/>) - the local body below carries only the
    /// five-hour bucket, the web body carries both, so the web answer is the one that reaches the
    /// tile.</summary>
    [Fact]
    public async Task The_web_answer_wins_over_the_local_read_when_both_are_usable()
    {
        const string localOneWindow = """
            {"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"2026-09-13T20:40:15Z","remainingFraction":0.5}]}]}
            """;
        const string webTwoWindows = """
            {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.0,1,[[1792246090,0]]],[45607,0.6,2,[[1792652890,0]]]],false]"}
            """;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, localOneWindow, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => webTwoWindows, out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.True(snapshot.WebSessionSignedIn);
        // The web answer carries no plan; the plan of the local sign-in stays with the tile.
        Assert.Equal("Google AI Pro", snapshot.PlanType);
    }

    /// <summary>The local sign-in answers perfectly well while the browser session is signed out -
    /// the tile has to keep showing it and still be able to offer the sign-in that makes the web
    /// numbers live.</summary>
    [Fact]
    public async Task A_signed_out_web_session_leaves_the_local_numbers_standing_and_reports_itself()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => """{"status":"not_signed_in"}""", out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.False(snapshot.WebSessionSignedIn);
    }

    /// <summary>Before the first sign-in there is no session to read, and starting a browser process
    /// to find that out would cost a whole WebView2 host and a request to the provider's site for a
    /// user who may never want that route. The tile still has to learn it is signed out, or it could
    /// never offer the sign-in.</summary>
    [Fact]
    public async Task No_browser_session_is_started_before_the_first_sign_in()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => OkEnvelope, out var scriptRuns, profileFolderExists: false);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Empty(scriptRuns);
        Assert.False(snapshot.WebSessionSignedIn);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
    }

    [Fact]
    public void A_further_web_only_account_cannot_skip_the_web_session()
    {
        var descriptor = new WebSessionDescriptor(
            ProviderId: "gemini", BaseUrl: "https://gemini.google.com/usage",
            SignInUrl: "https://gemini.google.com", AllowedHosts: ["google.com"], ProfileFolderName: "gemini#2");
        var source = new WebUsageSource(descriptor, new GeminiUsageEndpoint(), (_, _) => throw new InvalidOperationException("must not be called"));

        var further = GeminiProvider.ForWebOnlyAccount("gemini#2", new AppSettings(), _ => { }, source);

        Assert.False(further.CanSkipWebSession);
    }

    [Fact]
    public async Task Local_only_never_runs_the_web_read_and_turning_it_off_resumes_it()
    {
        NetworkStatus.Probe = () => true;
        var settings = new AppSettings();
        settings.Providers["gemini"] = new ProviderSettings { LocalOnly = true };
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => OkEnvelope, out var scriptRuns, settings: settings);
        Assert.True(provider.CanSkipWebSession);
        Assert.False(provider.SupportsInAppSignIn);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Empty(scriptRuns);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.False(snapshot.WebSessionSignedIn ?? false);

        settings.Providers["gemini"].LocalOnly = false;
        Assert.True(provider.SupportsInAppSignIn);
        await provider.FetchAsync(CancellationToken.None);
        Assert.NotEmpty(scriptRuns);
    }

    /// <summary>One page load per five minutes, however often the tile ticks: the local sign-in is
    /// free to re-read, the browser session is not.</summary>
    [Fact]
    public async Task The_web_session_is_read_once_per_interval_not_on_every_tick()
    {
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(usage, _ => OkEnvelope, out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Single(scriptRuns);

        now = now.AddMinutes(5);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(2, scriptRuns.Count);
    }

    /// <summary>The cached web answer is offered again under the new fetch time and flagged as a
    /// repeat, so it adds no second chart point and triggers no second notification.</summary>
    [Fact]
    public async Task A_cached_web_answer_is_marked_held_over_under_the_new_fetch_time()
    {
        const string localOneWindow = """
            {"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"2026-09-13T20:40:15Z","remainingFraction":0.5}]}]}
            """;
        const string webTwoWindows = """
            {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.0,1,[[1792246090,0]]],[45607,0.6,2,[[1792652890,0]]]],false]"}
            """;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, localOneWindow, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(usage, _ => webTwoWindows, out _, () => now);

        var first = await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(3);
        var cached = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, cached.SourceKind);
        Assert.False(first.HeldOver);
        Assert.True(cached.HeldOver);
        Assert.Equal(now, cached.FetchedAt);
        Assert.Equal(first.DataTimestamp, cached.DataTimestamp);
    }

    /// <summary>A browser session is only ever read once one exists on disk, so every test above runs
    /// against a fake profile root with the provider's own folder already in it. The one test above
    /// leaves that folder out on purpose.</summary>
    private GeminiProvider ProviderWithWebSession(
        AntigravityUsage usage, Func<string, string> answerScript, out List<string> scriptRuns,
        Func<DateTimeOffset>? now = null, bool profileFolderExists = true, AppSettings? settings = null)
    {
        var profileRoot = TestPaths.CreateDisposableDirectory("ai-usage-gemini-webview");
        _tempDirectories.Add(profileRoot);
        WebViewHost.SetRootOverride(profileRoot);
        if (profileFolderExists)
            Directory.CreateDirectory(Path.Combine(profileRoot, "gemini"));

        var runs = new List<string>();
        scriptRuns = runs;
        var clock = now ?? (() => DateTimeOffset.Parse("2026-09-18T20:00:00Z"));
        var descriptor = new WebSessionDescriptor(
            ProviderId: "gemini", BaseUrl: "https://gemini.google.com/usage",
            SignInUrl: "https://gemini.google.com/", AllowedHosts: ["google.com"], ProfileFolderName: "gemini");
        var source = new WebUsageSource(descriptor, new GeminiUsageEndpoint(), (script, _) =>
        {
            if (!WebViewHost.ProfileExists("gemini"))
                return Task.FromResult(NotSignedInEnvelope);
            runs.Add(script);
            return Task.FromResult(answerScript(script));
        });

        return new GeminiProvider((_) => Task.FromResult(usage), clock, source, settings ?? new AppSettings(), _ => { });
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    [Fact]
    public void A_reset_time_far_from_now_shows_no_countdown()
    {
        var windows = AiUsage.Providers.Parsing.GeminiUsageParser.Parse(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"9999-12-30T00:00:00Z","remainingFraction":0.5}]}]}""");

        var window = Assert.Single(windows);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public async Task After_a_failed_discovery_walk_the_page_is_left_alone_for_thirty_minutes()
    {
        NetworkStatus.Probe = () => true;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(usage, _ => """{"status":"failed","attempts":["/api/usage 404"]}""", out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        var afterFirst = scriptRuns.Count;
        Assert.True(afterFirst > 0);

        now = now.AddMinutes(10);
        await provider.FetchAsync(CancellationToken.None);
        now = now.AddMinutes(19);
        await provider.FetchAsync(CancellationToken.None);
        Assert.Equal(afterFirst, scriptRuns.Count);

        now = now.AddMinutes(2);
        await provider.FetchAsync(CancellationToken.None);
        Assert.True(scriptRuns.Count > afterFirst);
    }

    [Fact]
    public async Task A_finished_sign_in_ends_the_pause_after_a_failed_walk()
    {
        NetworkStatus.Probe = () => true;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(usage, _ => """{"status":"failed"}""", out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        var afterFirst = scriptRuns.Count;

        provider.SignInCompleted();
        now = now.AddMinutes(1);
        await provider.FetchAsync(CancellationToken.None);

        Assert.True(scriptRuns.Count > afterFirst);
    }

    [Fact]
    public async Task A_failed_walk_while_offline_does_not_start_the_pause()
    {
        NetworkStatus.Probe = () => false;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var provider = ProviderWithWebSession(usage, _ => """{"status":"failed"}""", out var scriptRuns, () => now);

        await provider.FetchAsync(CancellationToken.None);
        var afterFirst = scriptRuns.Count;

        now = now.AddMinutes(6);
        await provider.FetchAsync(CancellationToken.None);

        Assert.True(scriptRuns.Count > afterFirst);
    }

    /// <summary>The Antigravity read alone carries only the five-hour bucket, unused right after a
    /// reset. A web answer with both windows must still win, so that idle 0 % never hides the weekly
    /// window the usage page shows.</summary>
    [Fact]
    public async Task An_unused_five_hour_local_read_never_beats_a_web_answer_with_two_windows()
    {
        const string localUnused = """
            {"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","resetTime":"2026-09-18T23:00:00Z","remainingFraction":1.0}]}]}
            """;
        const string webTwoWindows = """
            {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.09,1,[[1792246090,0]]],[45607,0.06,2,[[1792652890,0]]]],false]"}
            """;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, localUnused, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => webTwoWindows, out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Equal(2, snapshot.Windows.Count);
    }

    /// <summary>The Antigravity per-model answer is the editor's own model quota, not the Gemini
    /// app's: with the app's usage page answering, its figures reach the tile even when it shows no
    /// more windows than the local read.</summary>
    [Fact]
    public async Task The_per_model_local_quota_never_beats_a_web_answer_with_one_window()
    {
        const string webOneWindow = """
            {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.09,1,[[1792246090,0]]]],false]"}
            """;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, SummaryWithoutGemini, "Pro", ModelQuotaJson: ModelQuota);
        var provider = ProviderWithWebSession(usage, _ => webOneWindow, out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Equal(9, Assert.Single(snapshot.Windows).UsedPercent, precision: 3);
    }

    /// <summary>Even a Gemini group in the Antigravity summary with both windows stays the editor's
    /// quota: a usable web answer is the Gemini app's own and wins.</summary>
    [Fact]
    public async Task A_usable_web_answer_wins_over_a_local_read_with_more_windows()
    {
        const string webOneWindow = """
            {"status":"ok","path":"/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc","body":"[2,[[2181,0.09,1,[[1792246090,0]]]],false]"}
            """;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var provider = ProviderWithWebSession(usage, _ => webOneWindow, out _);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Single(snapshot.Windows);
    }

    /// <summary>gemini.google.com can send the hidden session to Google's traffic check instead of
    /// loading. That page is for a person, so the read has to come back as "signed out" (the tile then
    /// offers the sign-in window, where the check can be passed), keep the local numbers, and try again
    /// on the next tick instead of going quiet for half an hour like a failed read.</summary>
    [Fact]
    public async Task A_traffic_check_on_the_start_page_reads_as_signed_out_and_is_retried_next_tick()
    {
        NetworkStatus.Probe = () => true;
        var usage = new AntigravityUsage(LocalLoginOutcome.Ok, QuotaSummary, "Google AI Pro");
        var now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var starts = 0;
        var runner = new WebSessionScriptRunner(() => { starts++; return new TrafficCheckHost(); }, providerId: "gemini", profileExists: () => true);
        var descriptor = new WebSessionDescriptor(
            ProviderId: "gemini", BaseUrl: "https://gemini.google.com/usage",
            SignInUrl: "https://gemini.google.com/", AllowedHosts: ["google.com"], ProfileFolderName: "gemini");
        var source = new WebUsageSource(descriptor, new GeminiUsageEndpoint(), runner.ExecuteScriptAsync);
        var provider = new GeminiProvider((_) => Task.FromResult(usage), () => now, source, new AppSettings(), _ => { });

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.LocalLogin, snapshot.SourceKind);
        Assert.False(snapshot.WebSessionSignedIn);
        var afterFirst = starts;
        Assert.True(afterFirst > 0);

        now = now.AddMinutes(6);
        await provider.FetchAsync(CancellationToken.None);

        Assert.True(starts > afterFirst);
        await runner.DisposeAsync();
    }

    /// <summary>A hidden host whose start page is turned into Google's traffic check.</summary>
    private sealed class TrafficCheckHost : IHiddenBrowserHost
    {
        public int? BrowserProcessId => null;

        public event Action? ProcessFailed { add { } remove { } }

        public Task<bool> NavigateAsync(CancellationToken ct) => Task.FromException<bool>(new WebSessionCheckRequiredException());

        public Task<string> ExecuteScriptAsync(string script, CancellationToken ct) =>
            throw new InvalidOperationException("No script runs on a host that never loaded.");

        public Task SuspendAsync() => Task.CompletedTask;

        public void Resume()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
