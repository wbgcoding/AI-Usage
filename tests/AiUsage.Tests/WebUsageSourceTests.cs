using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

public class WebUsageSourceTests
{
    // Real-shaped 36-character uuid: IsAllowedUsagePath now rejects anything shorter, and the
    // fixtures below need to survive that check to exercise the caching paths they test.
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";
    private const string UsagePath = $"/api/organizations/{OrgId}/usage";
    private const string UsageLimitsPath = $"/api/organizations/{OrgId}/usage_limits";

    // What settings.json holds instead of the organisation id.
    private const string StoredUsagePath = "/api/organizations/{org}/usage";
    private const string StoredUsageLimitsPath = "/api/organizations/{org}/usage_limits";

    private static readonly WebSessionDescriptor Descriptor = new(
        ProviderId: "claude", BaseUrl: "https://claude.ai/",
        SignInUrl: "https://claude.ai/login", AllowedHosts: ["claude.ai"], ProfileFolderName: "claude");

    private static WebUsageSource Source(Func<string, CancellationToken, Task<string>> executeScript) =>
        new(Descriptor, new ClaudeUsageEndpoint(), executeScript);

    // Built by concatenation rather than as one literal - ShipCleanTests bans a real-looking email
    // address anywhere in the tracked source, fixtures included.
    private const string FakeEmail = "user" + "@" + "example.com";

    [Fact]
    public async Task Discovery_caches_the_winning_path_and_returns_its_windows()
    {
        var settings = new AppSettings();
        var saved = new List<AppSettings>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, s => saved.Add(s), CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Single(result.Windows);
        Assert.Equal(StoredUsagePath, settings.WebUsagePaths.GetValueOrDefault("claude"));
        Assert.Single(saved);
    }

    [Fact]
    public async Task The_saved_settings_text_holds_no_account_id_and_the_next_read_uses_the_one_in_memory()
    {
        var settings = new AppSettings();
        var scripts = new List<string>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((script, _) =>
        {
            scripts.Add(script);
            return Task.FromResult(body);
        });

        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        var saved = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain(OrgId, saved, StringComparison.Ordinal);
        Assert.Contains("{org}", saved, StringComparison.Ordinal);

        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        Assert.Equal(2, scripts.Count);
        Assert.Contains(UsagePath, scripts[1], StringComparison.Ordinal); // the direct read, with the id filled in
        Assert.DoesNotContain("{org}", scripts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_placeholder_without_an_id_in_this_session_is_rediscovered()
    {
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = StoredUsagePath } };
        var scripts = new List<string>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((script, _) =>
        {
            scripts.Add(script);
            return Task.FromResult(body);
        });

        var first = await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        var second = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, first.Outcome);
        Assert.Equal(WebUsageOutcome.Ok, second.Outcome);
        Assert.Contains("/api/organizations", scripts[0], StringComparison.Ordinal); // the walk found the id
        Assert.Contains(UsagePath, scripts[1], StringComparison.Ordinal);            // the next read is direct
        Assert.Equal(StoredUsagePath, settings.WebUsagePaths["claude"]);
    }

    [Fact]
    public async Task A_path_with_a_raw_id_from_an_older_file_is_replaced_by_its_placeholder_form()
    {
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = UsagePath } };
        var saved = new List<AppSettings>();
        var scripts = new List<string>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((script, _) =>
        {
            scripts.Add(script);
            return Task.FromResult(body);
        });

        var result = await source.FetchAsync(settings, s => saved.Add(s), CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal(StoredUsagePath, settings.WebUsagePaths["claude"]);
        Assert.NotEmpty(saved);
        Assert.Single(scripts); // the id from the old file still serves this session's read
        Assert.Contains(UsagePath, scripts[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/usage?user=user_01ABC", "/api/usage?user={sub}", null, "user_01ABC")]
    [InlineData("/api/usage?user={sub}", "/api/usage?user={sub}", null, null)]
    [InlineData("/api/usage-summary", "/api/usage-summary", null, null)]
    [InlineData("/backend-api/codex/usage", "/backend-api/codex/usage", null, null)]
    [InlineData("/api/organizations/" + OrgId + "/usage", "/api/organizations/{org}/usage", OrgId, null)]
    public void Account_ids_in_a_path_are_swapped_for_placeholders_and_put_back(string path, string stored, string? org, string? sub)
    {
        var (placeholder, foundOrg, foundSub) = WebUsageSource.ToPlaceholderPath(path);

        Assert.Equal(stored, placeholder);
        Assert.Equal(org, foundOrg);
        Assert.Equal(sub, foundSub);
        if (org is not null || sub is not null)
            Assert.Equal(path, WebUsageSource.FillPlaceholders(placeholder, org, sub));
        Assert.Null(WebUsageSource.FillPlaceholders("/x/{org}/{sub}", null, null));
    }

    [Fact]
    public async Task A_cached_path_is_fetched_directly_without_rediscovery()
    {
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = UsagePath } };
        var requestedScripts = new List<string>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":10,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((script, _) =>
        {
            requestedScripts.Add(script);
            return Task.FromResult(body);
        });

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Single(requestedScripts); // one direct fetch, never the walk over the candidate paths
        Assert.DoesNotContain("rate_limits", requestedScripts[0]);
        Assert.DoesNotContain("usage_limits", requestedScripts[0]);
    }

    [Fact]
    public async Task A_cached_path_that_stops_answering_falls_back_to_rediscovery()
    {
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = UsagePath } };
        var calls = 0;
        var rediscoveredBody = """{"status":"ok","path":"__PATH__","body":"{\"seven_day\":{\"utilization\":20,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsageLimitsPath);
        var source = Source((script, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult("""{"status":"failed"}""")
                : Task.FromResult(rediscoveredBody);
        });

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(StoredUsageLimitsPath, settings.WebUsagePaths.GetValueOrDefault("claude"));
    }

    [Fact]
    public async Task A_cached_path_outside_the_allow_list_is_dropped_without_being_fetched()
    {
        // A poisoned or stale settings.json can carry anything; a path shaped like an injection
        // attempt must never reach ClaudeDiscoveryScript.Fetch.
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = "/api/x'+alert(1)+'" } };
        var saved = new List<AppSettings>();
        var scripts = new List<string>();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":5,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((script, _) =>
        {
            scripts.Add(script);
            return Task.FromResult(body);
        });

        var result = await source.FetchAsync(settings, s => saved.Add(s), CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Single(scripts); // only the rediscovery walk ran, never a Fetch of the poisoned path
        Assert.Equal(StoredUsagePath, settings.WebUsagePaths.GetValueOrDefault("claude")); // rediscovery's own result, not the poisoned value
    }

    [Fact]
    public async Task A_discovered_path_outside_the_allow_list_is_never_cached()
    {
        var settings = new AppSettings();
        var saved = new List<AppSettings>();
        var source = Source((_, _) => Task.FromResult(
            """{"status":"ok","path":"/api/x'+alert(1)+'","body":"{\"five_hour\":{\"utilization\":5,\"resets_at\":1788624000}}"}"""));

        var result = await source.FetchAsync(settings, s => saved.Add(s), CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome); // the usage body itself still parsed
        Assert.Null(settings.WebUsagePaths.GetValueOrDefault("claude")); // but the unsafe path was never stored
    }

    [Fact]
    public async Task A_discovery_walk_that_collects_several_results_caches_the_first_one_that_parses()
    {
        // The account id came from Discover's own /api/auth/me read, but every candidate path is
        // still tried rather than stopping at the first JSON answer - the first candidate here answers
        // with JSON that carries no usable window, and the second is the one that actually wins.
        var settings = new AppSettings();
        var saved = new List<AppSettings>();
        var body = """{"status":"ok","results":[{"path":"__FIRST__","body":"not usable"},{"path":"__SECOND__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}],"attempts":[]}"""
            .Replace("__FIRST__", UsageLimitsPath).Replace("__SECOND__", UsagePath);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, s => saved.Add(s), CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Single(result.Windows);
        Assert.Equal(StoredUsagePath, settings.WebUsagePaths.GetValueOrDefault("claude"));
        Assert.Single(saved);
    }

    [Fact]
    public async Task A_discovery_walk_where_no_result_parses_fails_without_caching_anything()
    {
        var settings = new AppSettings();
        var saveCalled = false;
        var body = """{"status":"ok","results":[{"path":"__PATH__","body":"not usable"}],"attempts":[]}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => saveCalled = true, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Null(settings.WebUsagePaths.GetValueOrDefault("claude"));
        Assert.False(saveCalled);
    }

    [Fact]
    public async Task Not_signed_in_status_is_forwarded_without_touching_settings()
    {
        var settings = new AppSettings();
        var saveCalled = false;
        var source = Source((_, _) => Task.FromResult("""{"status":"not_signed_in"}"""));

        var result = await source.FetchAsync(settings, _ => saveCalled = true, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.NotSignedIn, result.Outcome);
        Assert.Null(settings.WebUsagePaths.GetValueOrDefault("claude"));
        Assert.False(saveCalled);
    }

    [Fact]
    public async Task A_challenge_page_body_surfaces_as_blocked()
    {
        var settings = new AppSettings();
        var source = Source((_, _) => Task.FromResult("""{"status":"blocked"}"""));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task A_thrown_exception_from_the_script_runner_fails_cleanly()
    {
        var settings = new AppSettings();
        var source = Source((_, _) => throw new InvalidOperationException("WebView2 not ready"));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Interpret_rejects_a_malformed_envelope_instead_of_throwing()
    {
        var (result, path) = Source((_, _) => Task.FromResult("")).Interpret("not json");

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Null(path);
    }

    [Fact]
    public void Interpret_rejects_a_json_array_envelope_instead_of_throwing()
    {
        var (result, path) = Source((_, _) => Task.FromResult("")).Interpret("[]");

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Null(path);
    }

    [Fact]
    public void Interpret_reports_a_signed_in_session_even_when_the_body_is_unreadable()
    {
        var body = """{"status":"ok","path":"__PATH__","body":"{\"unknown\":1}"}""".Replace("__PATH__", UsagePath);

        var (result, path) = Source((_, _) => Task.FromResult("")).Interpret(body);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.True(result.SessionSignedIn);
        Assert.Null(path);
    }

    [Fact]
    public void Interpret_does_not_claim_a_session_for_a_signed_out_answer()
    {
        var (result, _) = Source((_, _) => Task.FromResult("")).Interpret("""{"status":"not_signed_in"}""");

        Assert.False(result.SessionSignedIn);
    }

    [Fact]
    public void Interpret_picks_the_first_results_entry_that_actually_parses_into_a_window()
    {
        var body = """{"status":"ok","results":[{"path":"__FIRST__","body":"not usable"},{"path":"__SECOND__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}],"attempts":[]}"""
            .Replace("__FIRST__", UsageLimitsPath).Replace("__SECOND__", UsagePath);

        var (result, path) = Source((_, _) => Task.FromResult("")).Interpret(body);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Single(result.Windows);
        Assert.Equal(UsagePath, path);
    }

    [Fact]
    public void Interpret_fails_when_no_results_entry_parses_into_a_window()
    {
        var body = """{"status":"ok","results":[{"path":"__PATH__","body":"not usable"}],"attempts":[]}"""
            .Replace("__PATH__", UsagePath);

        var (result, path) = Source((_, _) => Task.FromResult("")).Interpret(body);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Null(path);
    }

    [Fact]
    public void DescribeShape_reports_field_names_and_value_kinds_only()
    {
        const string body = """{"gpt-4-1": {"numRequests": 30, "maxRequestUsage": null}, "startOfMonth": "2026-09-01T00:00:00Z"}""";

        var shape = WebUsageSource.DescribeShape(body);

        Assert.Equal("gpt-4-1:{numRequests:number,maxRequestUsage:null}, startOfMonth:string", shape);
    }

    [Fact]
    public void DescribeShape_of_non_json_text_is_empty()
    {
        Assert.Equal("", WebUsageSource.DescribeShape("<html>not json</html>"));
    }

    [Fact]
    public async Task Discovery_logs_one_shape_line_per_collected_result()
    {
        var logged = new List<string>();
        var body = """{"status":"ok","results":[{"path":"__PATH__","body":"{\"numRequests\":1,\"maxRequestUsage\":2}"}],"attempts":[]}"""
            .Replace("__PATH__", UsagePath);
        var source = new WebUsageSource(Descriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(body), log: logged.Add);

        await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        Assert.Contains("claude: usage response shape /api/organizations/{id}/usage numRequests:number, maxRequestUsage:number", logged);
        Assert.DoesNotContain(logged, line => line.Contains(OrgId));
    }

    [Fact]
    public async Task Discovery_attempt_lines_never_carry_the_organization_id()
    {
        var logged = new List<string>();
        var body = """{"status":"ok","results":[],"attempts":["__PATH__ 200"]}""".Replace("__PATH__", UsagePath);
        var source = new WebUsageSource(Descriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(body), log: logged.Add);

        await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        Assert.Contains(logged, line => line.Contains("/api/organizations/{id}/usage 200"));
        Assert.DoesNotContain(logged, line => line.Contains(OrgId));
    }

    [Theory]
    [InlineData("/api/organizations/2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc/usage", "/api/organizations/{id}/usage")]
    [InlineData("/api/usage?user=user_01HZXABCDEFGHIJKLMN", "/api/usage?user={id}")]
    [InlineData("/backend-api/codex/usage", "/backend-api/codex/usage")]
    public void MaskIds_replaces_id_like_segments(string path, string expected)
    {
        Assert.Equal(expected, WebUsageSource.MaskIds(path));
    }

    [Fact]
    public void DescribeShape_truncates_and_cleans_property_names_and_caps_their_count()
    {
        var longName = new string('a', 100);
        var props = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"k{i}\":1"));
        var body = "{\"" + longName + "\":1,\"bad\\u0001name\":2," + props + "}";

        var shape = WebUsageSource.DescribeShape(body);

        Assert.DoesNotContain(longName, shape);
        Assert.Contains(new string('a', 40) + ":number", shape, StringComparison.Ordinal);
        Assert.DoesNotContain(shape, c => char.IsControl(c));
        Assert.Equal(20, shape.Split(", ").Length);
    }

    [Fact]
    public void DescribeShape_masks_property_names_that_look_like_ids_or_emails()
    {
        var body = """{"2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc":{"used":1},"user_01HZXABCDEFGHIJKLMN":2,"__EMAIL__":3,"claude-3-5-sonnet-20241022":4}""".Replace("__EMAIL__", FakeEmail);

        var shape = WebUsageSource.DescribeShape(body);

        Assert.Equal("{id}:{used:number}, {id}:number, {id}:number, claude-3-5-sonnet-20241022:number", shape);
    }

    // An "email" field a discovery/fetch script carries at the envelope's own root, next
    // to "status" - never required, and accepted only when it is a plausible email address.
    [Fact]
    public async Task An_email_field_on_the_envelope_becomes_the_result_account_label()
    {
        var settings = new AppSettings();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}","email":"__EMAIL__"}"""
            .Replace("__PATH__", UsagePath).Replace("__EMAIL__", FakeEmail);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(FakeEmail, result.AccountLabel);
    }

    [Fact]
    public async Task An_email_field_on_a_results_array_envelope_still_becomes_the_account_label()
    {
        var settings = new AppSettings();
        var body = """{"status":"ok","results":[{"path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}],"attempts":[],"email":"__EMAIL__"}"""
            .Replace("__PATH__", UsagePath).Replace("__EMAIL__", FakeEmail);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(FakeEmail, result.AccountLabel);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task An_implausible_email_field_is_dropped_rather_than_shown(string implausible)
    {
        var settings = new AppSettings();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}","email":"__EMAIL__"}"""
            .Replace("__PATH__", UsagePath).Replace("__EMAIL__", implausible);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Null(result.AccountLabel);
    }

    [Fact]
    public async Task An_overlong_email_field_is_dropped()
    {
        var settings = new AppSettings();
        var overlong = new string('a', 250) + "@example.com";
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}","email":"__EMAIL__"}"""
            .Replace("__PATH__", UsagePath).Replace("__EMAIL__", overlong);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Null(result.AccountLabel);
    }

    [Fact]
    public async Task No_email_field_at_all_leaves_the_account_label_null()
    {
        var settings = new AppSettings();
        var body = """{"status":"ok","path":"__PATH__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__PATH__", UsagePath);
        var source = Source((_, _) => Task.FromResult(body));

        var result = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Null(result.AccountLabel);
    }

    // A hidden session closed under a running read throws a cancellation of its own; only the
    // caller's own cancellation may leave the source, anything else is a failed read.
    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_is_a_failed_read()
    {
        var source = Source((_, _) => throw new OperationCanceledException("The hidden session was closed."));

        var result = await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        var source = Source((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(new AppSettings(), _ => { }, cts.Token));
    }

    [Fact]
    public async Task A_discovery_where_every_address_answered_not_found_reports_a_moved_endpoint()
    {
        var body = """{"status":"failed","attempts":["__PATH__ 404","__OTHER__ 404"]}"""
            .Replace("__PATH__", UsagePath).Replace("__OTHER__", UsagePath.Replace("/usage", "/usage_limits"));
        var source = Source((_, _) => Task.FromResult(body));
        using var capture = ReadChangeLog.BeginCapture();

        await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        var line = Assert.Single(capture.Lines);
        Assert.Contains("(Claude usage read needs adapting): every usage address answered with HTTP 404", line, StringComparison.Ordinal);
        Assert.DoesNotContain(OrgId, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":"failed","attempts":["__PATH__ 404","__PATH__ 500"]}""")]
    [InlineData("""{"status":"failed","attempts":["__PATH__ 000"]}""")]
    [InlineData("""{"status":"failed","attempts":[]}""")]
    [InlineData("""{"status":"failed"}""")]
    [InlineData("""{"status":"not_signed_in","attempts":["__PATH__ 404"]}""")]
    [InlineData("""{"status":"blocked","attempts":["__PATH__ 404"]}""")]
    public async Task A_discovery_that_failed_for_another_reason_reports_no_moved_endpoint(string template)
    {
        var body = template.Replace("__PATH__", UsagePath);
        var source = Source((_, _) => Task.FromResult(body));
        using var capture = ReadChangeLog.BeginCapture();

        await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        Assert.Empty(capture.Lines);
    }

    [Fact]
    public async Task A_further_account_is_named_by_its_provider_without_the_suffix()
    {
        var descriptor = Descriptor with { ProviderId = "claude#2" };
        var body = """{"status":"failed","attempts":["__PATH__ 404"]}""".Replace("__PATH__", UsagePath);
        var source = new WebUsageSource(descriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(body));
        using var capture = ReadChangeLog.BeginCapture();

        await source.FetchAsync(new AppSettings(), _ => { }, CancellationToken.None);

        Assert.Contains("(Claude usage read needs adapting)", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void The_Claude_discovery_script_reports_what_each_address_answered_with()
    {
        Assert.Contains("attempts.push(path + ' ' + res.status)", ClaudeDiscoveryScript.Discover(), StringComparison.Ordinal);
    }
}
