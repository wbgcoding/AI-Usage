using AiUsage.Models;
using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Cursor answers in a different shape per plan: the usage summary current plans are billed by, and
/// the request counters of the old request-based plans. Anything else stays silent - proven here
/// against fixtures shaped like each.
/// </summary>
public class CursorUsageParserTests
{
    [Fact]
    public void A_usage_summary_without_a_model_split_reads_the_plans_own_percent_and_resets_at_the_cycle_end()
    {
        const string json = """
            {"billingCycleStart": "2026-09-05T10:00:00.000Z", "billingCycleEnd": "2026-10-05T10:00:00.000Z",
             "membershipType": "pro", "isUnlimited": false,
             "individualUsage": {"plan": {"enabled": true, "used": 1250, "limit": 2000, "remaining": 750,
                                          "totalPercentUsed": 62.5},
                                 "onDemand": {"enabled": false, "used": 0, "limit": null, "remaining": null}}}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));

        Assert.Equal("Window_Month", window.Label);
        Assert.Equal(WindowKind.Other, window.Kind);
        Assert.Equal(62.5, window.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T10:00:00Z"), window.ResetsAt);
    }

    [Fact]
    public void A_usage_summary_with_a_model_split_emits_a_window_per_bar_instead_of_the_combined_one()
    {
        // Sanitised shape of the live 2026-09-26 probe: /api/usage-summary with both
        // autoPercentUsed and apiPercentUsed present, no grokBot merged in.
        const string json = """
            {"billingCycleStart": "2026-09-18T20:25:48.000Z", "billingCycleEnd": "2026-10-18T20:25:48.000Z",
             "membershipType": "pro", "limitType": "user", "isUnlimited": false,
             "individualUsage": {"plan": {"enabled": true, "used": 2000, "limit": 2000,
                                          "remaining": 0,
                                          "breakdown": {"included": 2000, "bonus": 15376, "total": 17376},
                                          "autoPercentUsed": 29.491111111111113, "apiPercentUsed": 91.22222222222223,
                                          "totalPercentUsed": 35.1030303030303},
                                 "onDemand": {"enabled": false, "used": 0, "limit": null, "remaining": null}},
             "teamUsage": {}}
            """;

        var windows = CursorUsageParser.Parse(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal("Window_CursorModels", windows[0].Label);
        Assert.Equal(WindowKind.Other, windows[0].Kind);
        Assert.Equal(29.491111111111113, windows[0].UsedPercent, precision: 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T20:25:48Z"), windows[0].ResetsAt);
        Assert.Equal("Window_OtherModels", windows[1].Label);
        Assert.Equal(WindowKind.Other, windows[1].Kind);
        Assert.Equal(91.22222222222223, windows[1].UsedPercent, precision: 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T20:25:48Z"), windows[1].ResetsAt);
    }

    [Fact]
    public void A_merged_in_grok_bot_object_becomes_a_third_weekly_window_after_the_split()
    {
        const string json = """
            {"billingCycleEnd": "2026-10-18T20:25:48.000Z",
             "individualUsage": {"plan": {"autoPercentUsed": 29.5, "apiPercentUsed": 91.2}},
             "grokBot": {"usagePercent": 0, "nextResetTimestampUtc": "2026-10-02T20:35:33.260Z"}}
            """;

        var windows = CursorUsageParser.Parse(json);

        Assert.Equal(3, windows.Count);
        Assert.Equal("Window_CursorModels", windows[0].Label);
        Assert.Equal("Window_OtherModels", windows[1].Label);
        Assert.Equal("Window_GrokBotWeekly", windows[2].Label);
        Assert.Equal(WindowKind.Other, windows[2].Kind);
        Assert.Equal(0, windows[2].UsedPercent);
        Assert.Equal(10080, windows[2].WindowMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T20:35:33.260Z"), windows[2].ResetsAt);
    }

    [Fact]
    public void A_merged_in_grok_bot_object_rides_along_even_without_a_model_split()
    {
        const string json = """
            {"billingCycleEnd": "2026-10-18T20:25:48.000Z",
             "individualUsage": {"plan": {"totalPercentUsed": 35.1}},
             "grokBot": {"usagePercent": 12.5, "nextResetTimestampUtc": "2026-10-02T20:35:33.260Z"}}
            """;

        var windows = CursorUsageParser.Parse(json);

        Assert.Equal(2, windows.Count);
        Assert.Equal("Window_Month", windows[0].Label);
        Assert.Equal("Window_GrokBotWeekly", windows[1].Label);
        Assert.Equal(12.5, windows[1].UsedPercent, precision: 3);
    }

    [Fact]
    public void A_grok_bot_object_without_a_numeric_percent_is_ignored()
    {
        const string json = """
            {"individualUsage": {"plan": {"totalPercentUsed": 10}}, "grokBot": {"usagePercent": null}}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));
        Assert.Equal("Window_Month", window.Label);
    }

    [Fact]
    public void A_usage_summary_without_a_percent_field_divides_used_by_limit()
    {
        const string json = """
            {"billingCycleEnd": "2026-10-05T10:00:00Z",
             "individualUsage": {"plan": {"used": 500, "limit": 2000}}}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));

        Assert.Equal(25, window.UsedPercent, precision: 3);
    }

    [Fact]
    public void An_unlimited_usage_summary_without_numbers_reads_as_nothing_used()
    {
        const string json = """
            {"isUnlimited": true, "individualUsage": {"plan": {"enabled": true}}}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));

        Assert.Equal(0, window.UsedPercent);
    }

    [Fact]
    public void A_usage_summary_whose_plan_carries_no_numbers_yields_no_window()
    {
        const string json = """
            {"isUnlimited": false, "individualUsage": {"plan": {"enabled": true, "limit": 0}}}
            """;

        Assert.Empty(CursorUsageParser.Parse(json));
    }

    [Fact]
    public void The_request_counters_of_a_usage_based_plan_carry_no_limit_and_yield_no_window()
    {
        // The live shape of /api/usage on a current plan: one model, a request count, no limit.
        const string json = """
            {"gpt-4": {"numRequests": 12, "numRequestsTotal": 40, "numTokens": 9000, "maxTokenUsage": null, "maxRequestUsage": null},
             "startOfMonth": "2026-09-05T10:00:00.000Z"}
            """;

        Assert.Empty(CursorUsageParser.Parse(json));
    }

    [Fact]
    public void A_root_level_request_pair_with_an_iso_reset_becomes_one_month_window()
    {
        const string json = """
            {"numRequests": 420, "maxRequestUsage": 500, "startOfMonth": "2026-09-01T00:00:00Z", "totalTokens": 12345}
            """;

        var windows = CursorUsageParser.Parse(json);

        var window = Assert.Single(windows);
        Assert.Equal("Window_Month", window.Label);
        Assert.Equal(WindowKind.Other, window.Kind);
        Assert.Equal(84, window.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), window.ResetsAt);
        Assert.Equal(12345, window.Tokens?.TotalTokens);
    }

    [Fact]
    public void A_cycle_start_at_the_end_of_the_calendar_reads_as_no_reset_instead_of_throwing()
    {
        const string json = """
            {"numRequests": 420, "maxRequestUsage": 500, "startOfMonth": "9999-12-31T00:00:00Z"}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));

        Assert.Equal(84, window.UsedPercent, precision: 3);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void A_request_pair_nested_under_a_model_key_with_a_unix_reset_is_read_too()
    {
        const string json = """
            {"gpt-4-1": {"numRequests": 30, "maxRequestUsage": 120, "inputTokens": 100, "outputTokens": 50}, "startOfMonth": 1756684800}
            """;

        var windows = CursorUsageParser.Parse(json);

        var window = Assert.Single(windows);
        Assert.Equal(25, window.UsedPercent, precision: 3);
        Assert.Equal(DateTimeOffset.Parse("2025-10-01T00:00:00Z"), window.ResetsAt);
        Assert.Equal(150, window.Tokens?.TotalTokens);
    }

    [Fact]
    public void A_used_included_usage_cents_pair_becomes_one_month_window()
    {
        const string json = """{"usedIncludedUsageCents": 750, "includedUsageCents": 2000}""";

        var windows = CursorUsageParser.Parse(json);

        var window = Assert.Single(windows);
        Assert.Equal(37.5, window.UsedPercent, precision: 3);
        Assert.Null(window.ResetsAt);
        Assert.Null(window.Tokens);
    }

    [Fact]
    public void The_same_pair_without_the_cents_suffix_is_read_too()
    {
        const string json = """{"usedIncludedUsage": 5, "includedUsage": 20}""";

        var windows = CursorUsageParser.Parse(json);

        Assert.Equal(25, Assert.Single(windows).UsedPercent, precision: 3);
    }

    [Fact]
    public void A_limit_of_zero_yields_no_window()
    {
        const string json = """{"numRequests": 10, "maxRequestUsage": 0}""";

        Assert.Empty(CursorUsageParser.Parse(json));
    }

    [Fact]
    public void A_foreign_json_object_yields_no_window()
    {
        const string json = """{"unrelated": true, "count": 5}""";

        Assert.Empty(CursorUsageParser.Parse(json));
    }

    [Fact]
    public void Non_json_text_yields_no_window()
    {
        Assert.Empty(CursorUsageParser.Parse("<html>not json</html>"));
    }

    [Fact]
    public void Several_models_without_a_request_limit_sum_only_their_token_counts()
    {
        const string json = """
            {"gpt-4-1": {"numRequests": 10, "maxRequestUsage": null, "numTokens": 1000},
             "gpt-5": {"numRequests": 20, "maxRequestUsage": null, "numTokens": 2500}}
            """;

        var windows = CursorUsageParser.Parse(json);

        var window = Assert.Single(windows);
        Assert.Equal(0, window.UsedPercent);
        Assert.Equal(3500, window.Tokens?.TotalTokens);
        Assert.Null(window.Allowance);
    }

    [Fact]
    public void Several_models_with_request_limits_are_summed_not_read_from_the_first_alone()
    {
        const string json = """
            {"gpt-4-1": {"numRequests": 10, "maxRequestUsage": 100},
             "gpt-5": {"numRequests": 30, "maxRequestUsage": 200}}
            """;

        var windows = CursorUsageParser.Parse(json);

        var window = Assert.Single(windows);
        // 40 of 300, not 10 of 100 (the first model alone).
        Assert.Equal(40.0 / 3, window.UsedPercent, precision: 3);
        Assert.Equal(40, window.Allowance?.Used);
        Assert.Equal(300, window.Allowance?.Total);
        Assert.Equal("Unit.Requests", window.Allowance?.UnitKey);
    }

    // The older usage shape lists unlimited models with a null limit next to the limited one; their
    // requests used to land on the limited model's share and push it toward 100 %.
    [Fact]
    public void Requests_to_a_model_without_a_limit_do_not_count_against_the_limited_one()
    {
        const string json = """
            {"gpt-4": {"numRequests": 50, "maxRequestUsage": 500},
             "gpt-3.5-turbo": {"numRequests": 400, "maxRequestUsage": null},
             "gpt-4-32k": {"numRequests": 0, "maxRequestUsage": null}}
            """;

        var window = Assert.Single(CursorUsageParser.Parse(json));

        Assert.Equal(10, window.UsedPercent, precision: 3);
        Assert.Equal(50, window.Allowance?.Used);
        Assert.Equal(500, window.Allowance?.Total);
    }

    [Fact]
    public void Exactly_one_model_object_still_reads_as_it_always_has()
    {
        const string json = """{"gpt-4-1": {"numRequests": 30, "maxRequestUsage": 120}}""";

        var windows = CursorUsageParser.Parse(json);

        Assert.Equal(25, Assert.Single(windows).UsedPercent, precision: 3);
    }

    [Fact]
    public void A_body_with_none_of_the_recognised_fields_still_yields_no_window()
    {
        const string json = """{"gpt-4-1": {"somethingElse": 1}, "gpt-5": {"somethingElse": 2}}""";

        Assert.Empty(CursorUsageParser.Parse(json));
    }
}
