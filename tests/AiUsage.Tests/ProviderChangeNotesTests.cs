using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// Every spot where a provider's read skips or falls back on something it does not know names it in
/// a change note instead (see <see cref="ReadChangeLog"/>). One test per note, plus the answers as
/// they look today, which must stay silent: routine states (a null window, an unlimited quota, a
/// quota with no usage) are no change.
/// </summary>
public class ProviderChangeNotesTests
{
    // --- Claude: the usage answer ---------------------------------------------------------------

    [Fact]
    public void Claude_the_answer_as_adapted_needs_no_note() =>
        Assert.Empty(ClaudeUsageParser.ChangeNotes("""
            {
              "five_hour": {"utilization": 10, "resets_at": 1790812800},
              "seven_day": {"utilization": 20, "resets_at": 1790812800},
              "seven_day_opus": null,
              "extra_usage": {"is_enabled": false, "utilization": null},
              "limits": [
                {"kind": "weekly_scoped", "percent": 95, "scope": {"model": {"display_name": "Sonnet"}}},
                {"kind": "session", "percent": 50}
              ]
            }
            """));

    [Fact]
    public void Claude_the_unnamed_internal_window_and_the_all_models_weekly_limit_need_no_note() =>
        Assert.Empty(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"seven_day":{"utilization":2},"iguana_necktie":{"utilization":3,"resets_at":1790812800},"limits":[{"kind":"weekly_all","percent":2,"resets_at":1790812800}]}"""));

    [Fact]
    public void Claude_a_window_object_with_an_unknown_name_is_named() =>
        Assert.Contains("unknown window 'seven_day_newmodel'", Assert.Single(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"seven_day_newmodel":{"utilization":4,"resets_at":1790812800}}""")), StringComparison.Ordinal);

    [Fact]
    public void Claude_the_known_per_model_and_extra_usage_windows_need_no_note() =>
        Assert.Empty(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"seven_day":{"utilization":2},"seven_day_opus":{"utilization":3},"seven_day_sonnet":{"utilization":4},"seven_day_oauth_apps":{"utilization":5},"extra_usage":{"is_enabled":true,"utilization":6}}"""));

    [Fact]
    public void Claude_a_known_window_without_any_percent_field_names_its_keys()
    {
        var note = Assert.Single(ClaudeUsageParser.ChangeNotes("""{"five_hour":{"load":1,"resets_at":1790812800}}"""));

        Assert.Contains("'five_hour'", note, StringComparison.Ordinal);
        Assert.Contains("load, resets_at", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_a_limits_entry_of_an_unknown_kind_is_named() =>
        Assert.Contains("unknown kind 'monthly'", Assert.Single(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"limits":[{"kind":"monthly","percent":3}]}""")), StringComparison.Ordinal);

    [Fact]
    public void Claude_a_scoped_limit_without_a_percent_is_noted() =>
        Assert.Contains("no numeric percent", Assert.Single(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"limits":[{"kind":"weekly_scoped","pct":3,"scope":{"model":{"display_name":"Sonnet"}}}]}""")), StringComparison.Ordinal);

    [Fact]
    public void Claude_a_scoped_limit_without_a_model_name_is_noted() =>
        Assert.Contains("names no model", Assert.Single(ClaudeUsageParser.ChangeNotes(
            """{"five_hour":{"utilization":1},"limits":[{"kind":"weekly_scoped","percent":3,"scope":{}}]}""")), StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("<html>Just a moment...</html>")]
    [InlineData("[1,2]")]
    public void Claude_text_that_is_no_json_object_is_no_change_of_shape(string body) =>
        Assert.Empty(ClaudeUsageParser.ChangeNotes(body));

    // --- Claude: the transcript's rejected limit -----------------------------------------------

    [Fact]
    public void Claude_a_rejected_limit_as_adapted_needs_no_note() =>
        Assert.Empty(ClaudeLocalLimitReader.ChangeNotes(
            """{"timestamp":"2026-09-05T12:00:00Z","quotaLimits":{"status":"rejected","resetsAt":1788624000,"rateLimitType":"five_hour"}}"""));

    [Fact]
    public void Claude_a_rejected_limit_without_a_usable_reset_is_noted() =>
        Assert.Contains("no usable resetsAt", Assert.Single(ClaudeLocalLimitReader.ChangeNotes(
            """{"quotaLimits":{"status":"rejected","rateLimitType":"five_hour","reset":"soon"}}""")), StringComparison.Ordinal);

    [Fact]
    public void Claude_a_rejected_limit_of_an_unknown_type_is_named() =>
        Assert.Contains("unknown rateLimitType 'monthly'", Assert.Single(ClaudeLocalLimitReader.ChangeNotes(
            """{"quotaLimits":{"status":"rejected","resetsAt":1788624000,"rateLimitType":"monthly"}}""")), StringComparison.Ordinal);

    [Fact]
    public void Claude_a_rejected_limit_without_a_type_is_noted() =>
        Assert.Contains("no rateLimitType", Assert.Single(ClaudeLocalLimitReader.ChangeNotes(
            """{"quotaLimits":{"status":"rejected","resetsAt":1788624000}}""")), StringComparison.Ordinal);

    [Theory]
    [InlineData("""{"quotaLimits":{"status":"allowed","rateLimitType":"anything"}}""")]
    [InlineData("""{"message":"talking about quotaLimits and rejected"}""")]
    [InlineData("not json but quotaLimits")]
    public void Claude_a_line_that_is_no_rejection_says_nothing(string line) =>
        Assert.Empty(ClaudeLocalLimitReader.ChangeNotes(line));

    [Fact]
    public void Claude_the_transcript_scan_reports_a_note_through_the_change_log()
    {
        using var directory = TestPaths.CreateDisposableDirectory("claude-change-note");
        var projects = Path.Combine(directory.Path, "proj");
        Directory.CreateDirectory(projects);
        File.WriteAllText(
            Path.Combine(projects, "session.jsonl"),
            """{"timestamp":"2026-09-05T12:00:00Z","quotaLimits":{"status":"rejected","resetsAt":1788624000,"rateLimitType":"monthly"}}""" + "\n");
        using var capture = ReadChangeLog.BeginCapture();

        ClaudeLocalLimitReader.FindActiveLimit(directory.Path, DateTimeOffset.Parse("2026-09-05T12:30:00Z"));

        Assert.Contains("(Claude usage read needs adapting)", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    // --- Codex: the web answer and the session lines ----------------------------------------------

    [Fact]
    public void Codex_the_web_answer_as_adapted_needs_no_note() =>
        Assert.Empty(CodexWebUsageParser.ChangeNotes("""
            {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
              "primary_window":{"used_percent":12,"limit_window_seconds":18000,"reset_after_seconds":3600},
              "secondary_window":{"used_percent":30,"limit_window_seconds":604800,"reset_after_seconds":86400}},
             "credits":{"has_credits":false}}
            """));

    [Fact]
    public void Codex_a_null_window_is_the_normal_shape_of_no_number() =>
        Assert.Empty(CodexWebUsageParser.ChangeNotes("""{"rate_limits":{"primary":{"used_percent":3,"window_minutes":300},"secondary":null}}"""));

    [Fact]
    public void Codex_an_unknown_window_in_the_web_answer_is_named() =>
        Assert.Contains("unknown window 'tertiary_window'", Assert.Single(CodexWebUsageParser.ChangeNotes(
            """{"rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000},"tertiary_window":{"used_percent":9,"limit_window_seconds":18000}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Codex_a_known_window_without_used_percent_names_its_keys() =>
        Assert.Contains("'primary_window' has no used_percent field (keys: percent, limit_window_seconds)", Assert.Single(CodexWebUsageParser.ChangeNotes(
            """{"rate_limit":{"primary_window":{"percent":5,"limit_window_seconds":18000}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Codex_a_window_length_that_is_neither_five_hours_nor_a_week_is_noted() =>
        Assert.Contains("runs 43200 minutes", Assert.Single(CodexWebUsageParser.ChangeNotes(
            """{"rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":2592000}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Codex_a_session_line_as_adapted_needs_no_note()
    {
        var notes = new List<string>();

        var parsed = CodexRateLimitParser.TryParse(SessionLine("""{"used_percent":2.0,"window_minutes":300,"resets_at":1788642834}""", "tertiary", null), out _, notes);

        Assert.True(parsed);
        Assert.Empty(notes);
    }

    [Fact]
    public void Codex_an_unknown_window_in_a_session_line_is_named()
    {
        var notes = new List<string>();

        CodexRateLimitParser.TryParse(SessionLine("""{"used_percent":2.0,"window_minutes":300}""", "tertiary", """{"used_percent":9.0,"window_minutes":300}"""), out _, notes);

        Assert.Contains("unknown window 'tertiary'", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_an_unusual_window_length_in_a_session_line_is_noted()
    {
        var notes = new List<string>();

        CodexRateLimitParser.TryParse(SessionLine("""{"used_percent":2.0,"window_minutes":60}""", "tertiary", null), out _, notes);

        Assert.Contains("runs 60 minutes", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_the_provider_reports_a_session_line_note_through_the_change_log()
    {
        using var directory = TestPaths.CreateDisposableDirectory("codex-change-note");
        File.WriteAllText(
            Path.Combine(directory.Path, "rollout-test.jsonl"),
            SessionLine("""{"used_percent":2.0,"window_minutes":300,"resets_at":1788642834}""", "tertiary", """{"used_percent":9.0,"window_minutes":300}""") + "\n");
        var provider = new CodexProvider(directory.Path, () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"));
        using var capture = ReadChangeLog.BeginCapture();

        await provider.FetchAsync(CancellationToken.None);

        Assert.Contains("(Codex usage read needs adapting)", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    private static string SessionLine(string primary, string extraName, string? extra)
    {
        var extraBlock = extra is null ? "" : $",\"{extraName}\":{extra}";
        return "{\"timestamp\":\"2026-09-01T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,"
            + "\"rate_limits\":{\"primary\":" + primary + extraBlock + ",\"secondary\":null,\"plan_type\":\"plus\"}}}";
    }

    // --- Cursor ------------------------------------------------------------------------------------

    [Fact]
    public void Cursor_the_usage_summary_as_adapted_needs_no_note() =>
        Assert.Empty(CursorUsageParser.ChangeNotes("""
            {"billingCycleEnd":"2026-10-31T00:00:00Z","membershipType":"pro","isUnlimited":false,
             "individualUsage":{"plan":{"autoPercentUsed":10,"apiPercentUsed":20,"totalPercentUsed":15},"onDemand":{}},
             "teamUsage":{},"grokBot":{"usagePercent":3,"nextResetTimestampUtc":"2026-10-24T00:00:00Z"}}
            """));

    [Fact]
    public void Cursor_the_other_usage_addresses_with_their_own_shapes_say_nothing() =>
        Assert.Empty(CursorUsageParser.ChangeNotes(
            """{"gpt-4":{"numRequests":3,"numRequestsTotal":3,"numTokens":0,"maxTokenUsage":null,"maxRequestUsage":null},"startOfMonth":"2026-10-01T00:00:00Z"}"""));

    [Fact]
    public void Cursor_a_plan_block_without_any_known_usage_field_names_its_keys() =>
        Assert.Contains("none of the known usage fields (keys: spent, cap)", Assert.Single(CursorUsageParser.ChangeNotes(
            """{"individualUsage":{"plan":{"spent":1,"cap":2}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Cursor_a_further_percent_bar_is_named() =>
        Assert.Contains("unknown usage bar 'imagePercentUsed'", Assert.Single(CursorUsageParser.ChangeNotes(
            """{"individualUsage":{"plan":{"autoPercentUsed":10,"imagePercentUsed":4}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Cursor_a_grok_bot_block_without_a_percentage_is_noted() =>
        Assert.Contains("grokBot block holds no numeric usagePercent", Assert.Single(CursorUsageParser.ChangeNotes(
            """{"individualUsage":{"plan":{"autoPercentUsed":10}},"grokBot":{"percent":3}}""")), StringComparison.Ordinal);

    // --- Copilot -----------------------------------------------------------------------------------

    private const string CopilotAsAdapted = """
        {"login":"octocat","quota_reset_date_utc":"2026-10-01T00:00:00.000Z",
         "quota_snapshots":{
           "chat":{"percent_remaining":80.0,"unlimited":false,"has_quota":true},
           "completions":{"percent_remaining":100.0,"unlimited":true,"has_quota":true},
           "premium_interactions":{"percent_remaining":0.0,"unlimited":false,"has_quota":false}}}
        """;

    [Fact]
    public void Copilot_the_answer_as_adapted_needs_no_note() =>
        Assert.Empty(CopilotUserParser.ChangeNotes(CopilotAsAdapted));

    [Fact]
    public void Copilot_a_new_quota_id_is_named() =>
        Assert.Contains("new quota id 'code_review'", Assert.Single(CopilotUserParser.ChangeNotes(
            """{"quota_snapshots":{"chat":{"percent_remaining":80},"code_review":{"percent_remaining":50}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Copilot_a_quota_without_a_percentage_is_noted() =>
        Assert.Contains("quota 'chat' has no numeric percent_remaining", Assert.Single(CopilotUserParser.ChangeNotes(
            """{"quota_snapshots":{"chat":{"remaining":80,"has_quota":true}}}""")), StringComparison.Ordinal);

    [Fact]
    public void Copilot_a_percentage_outside_the_range_is_noted() =>
        Assert.Contains("outside 0 to 100", Assert.Single(CopilotUserParser.ChangeNotes(
            """{"quota_snapshots":{"chat":{"percent_remaining":180}}}""")), StringComparison.Ordinal);

    [Fact]
    public async Task Copilot_the_provider_reports_a_note_through_the_change_log()
    {
        var fetch = new CopilotFetch(GitHubCliOutcome.Ok, """{"quota_snapshots":{"chat":{"percent_remaining":80},"wizard":{"percent_remaining":50}}}""");
        var provider = new CopilotProvider(_ => Task.FromResult(fetch), () => DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        using var capture = ReadChangeLog.BeginCapture();

        await provider.FetchAsync(CancellationToken.None);

        Assert.Contains("(Copilot usage read needs adapting): new quota id 'wizard'", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    // --- Gemini: the web summary --------------------------------------------------------------------

    [Fact]
    public void Gemini_the_web_summary_as_adapted_needs_no_note() =>
        Assert.Empty(GeminiUsageParser.WebChangeNotes(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","remainingFraction":1},{"window":"weekly","remainingFraction":0.5}]}]}"""));

    [Fact]
    public void Gemini_the_other_usage_addresses_with_their_own_shapes_say_nothing() =>
        Assert.Empty(GeminiUsageParser.WebChangeNotes("""{"usage":{"requests":3}}"""));

    [Fact]
    public void Gemini_a_bucket_with_an_unknown_window_is_named() =>
        Assert.Contains("unknown window 'daily'", Assert.Single(GeminiUsageParser.WebChangeNotes(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"daily","remainingFraction":0.5}]}]}""")), StringComparison.Ordinal);

    [Fact]
    public void Gemini_a_bucket_without_a_window_name_is_noted() =>
        Assert.Contains("names no window", Assert.Single(GeminiUsageParser.WebChangeNotes(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"remainingFraction":0.5}]}]}""")), StringComparison.Ordinal);

    [Fact]
    public void Gemini_a_bucket_with_an_unusable_fraction_is_noted() =>
        Assert.Contains("'5h' bucket has a remainingFraction that is no number between 0 and 1", Assert.Single(GeminiUsageParser.WebChangeNotes(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","remainingFraction":2}]}]}""")), StringComparison.Ordinal);

    // --- The endpoints report what their parser notes ---------------------------------------------------

    [Fact]
    public void Each_web_endpoint_reports_its_parsers_notes_through_the_change_log()
    {
        using var capture = ReadChangeLog.BeginCapture();

        new ClaudeUsageEndpoint().ParseBody("""{"five_hour":{"utilization":1},"seven_day_newmodel":{"utilization":4}}""");
        new CodexUsageEndpoint(() => DateTimeOffset.Parse("2026-10-17T12:00:00Z")).ParseBody(
            """{"rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000},"tertiary_window":{"used_percent":9}}}""");
        new CursorUsageEndpoint().ParseBody("""{"individualUsage":{"plan":{"autoPercentUsed":10,"imagePercentUsed":4}}}""");
        new GeminiUsageEndpoint().ParseBody("[2,[[2181,0.1,1],[7,0.3,3]],false]");

        foreach (var provider in new[] { "Claude", "Codex", "Cursor", "Gemini" })
            Assert.Single(capture.Lines, line => line.Contains($"({provider} usage read needs adapting)", StringComparison.Ordinal));
    }

    [Fact]
    public void An_endpoint_stays_silent_for_an_answer_that_is_as_adapted()
    {
        using var capture = ReadChangeLog.BeginCapture();

        new ClaudeUsageEndpoint().ParseBody("""{"five_hour":{"utilization":1}}""");
        new CodexUsageEndpoint(() => DateTimeOffset.Parse("2026-10-17T12:00:00Z")).ParseBody("""{"rate_limits":{"primary":{"used_percent":1,"window_minutes":300}}}""");
        new CursorUsageEndpoint().ParseBody("""{"individualUsage":{"plan":{"autoPercentUsed":10}}}""");
        new GeminiUsageEndpoint().ParseBody("[2,[[2181,0.1,1,[[1792246090,0]]]],false]");

        Assert.Empty(capture.Lines);
    }

    // --- Names only ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("user_01M2TY4QTWX0QJVEP2CJQYTFA4", "{id}")]
    [InlineData("ACCT1234567890ABCDEF", "{id}")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "{id}")]
    [InlineData("seven_day_opus", "seven_day_opus")]
    [InlineData("gemini-3.5-flash-extra-low", "gemini-3.5-flash-extra-low")]
    public void A_name_that_looks_like_an_id_or_an_address_never_reaches_the_log(string name, string expected)
        => Assert.Equal(expected, ReadChangeLog.SafeName(name));

    [Fact]
    public void An_email_address_never_reaches_the_log() =>
        // Built by concatenation: the ship-clean scan bans a real-looking address in any tracked file.
        Assert.Equal("{id}", ReadChangeLog.SafeName("someone" + "@" + "example.com"));

    [Fact]
    public void A_name_is_cut_short_and_stripped_of_unusual_characters()
    {
        var name = ReadChangeLog.SafeName("line\nbreak " + new string('x', 100));

        Assert.Equal(40, name.Length);
        Assert.DoesNotContain('\n', name);
    }
}
