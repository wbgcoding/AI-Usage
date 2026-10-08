using System.Linq;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeUsageParserTests
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixturesDirectory, name));

    [Fact]
    public void A_usage_response_yields_the_five_hour_and_weekly_windows()
    {
        var result = ClaudeUsageParser.Parse(ReadFixture("claude-usage.json"));

        Assert.Equal(ClaudeUsageOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Windows.Count);
        var fiveHour = Assert.Single(result.Windows, w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(42, fiveHour.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788624000), fiveHour.ResetsAt);
        var weekly = Assert.Single(result.Windows, w => w.Kind == WindowKind.Weekly);
        Assert.Equal(68, weekly.UsedPercent);
    }

    [Fact]
    public void A_challenge_page_is_recognised_by_its_leading_angle_bracket()
    {
        var result = ClaudeUsageParser.Parse(ReadFixture("claude-challenge.html"));

        Assert.Equal(ClaudeUsageOutcome.Blocked, result.Outcome);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void A_challenge_page_behind_a_byte_order_mark_is_still_recognised()
    {
        var result = ClaudeUsageParser.Parse("﻿<html><body>please wait</body></html>");

        Assert.Equal(ClaudeUsageOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void A_json_typed_body_with_a_just_a_moment_challenge_is_recognised_as_blocked()
    {
        var result = ClaudeUsageParser.Parse("Just a moment while we verify you are human.");

        Assert.Equal(ClaudeUsageOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void A_json_typed_body_asking_to_enable_javascript_is_recognised_as_blocked()
    {
        var result = ClaudeUsageParser.Parse("Enable JavaScript and cookies to continue.");

        Assert.Equal(ClaudeUsageOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void A_real_looking_usage_body_still_parses_as_ok()
    {
        var result = ClaudeUsageParser.Parse("""{"five_hour":{"utilization":42,"resets_at":"..."}}""");

        Assert.Equal(ClaudeUsageOutcome.Ok, result.Outcome);
        var window = Assert.Single(result.Windows);
        Assert.Equal(42, window.UsedPercent);
    }

    [Fact]
    public void Empty_text_fails_cleanly_instead_of_throwing()
    {
        var result = ClaudeUsageParser.Parse("");

        Assert.Equal(ClaudeUsageOutcome.Failed, result.Outcome);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void An_unrecognised_json_shape_fails_rather_than_guessing()
    {
        var result = ClaudeUsageParser.Parse("""{"unrelated":"field"}""");

        Assert.Equal(ClaudeUsageOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Malformed_json_fails_cleanly()
    {
        var result = ClaudeUsageParser.Parse("{not json");

        Assert.Equal(ClaudeUsageOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void A_json_array_root_fails_cleanly_instead_of_throwing()
    {
        var result = ClaudeUsageParser.Parse("[]");

        Assert.Equal(ClaudeUsageOutcome.Failed, result.Outcome);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void A_reset_time_outside_range_is_dropped_while_a_valid_percentage_still_wins()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":99999999999999}}""");

        Assert.Equal(ClaudeUsageOutcome.Ok, result.Outcome);
        var window = Assert.Single(result.Windows);
        Assert.Equal(42, window.UsedPercent);
        Assert.Null(window.ResetsAt);
    }

    private static readonly DateTimeOffset ExpectedReset = DateTimeOffset.FromUnixTimeSeconds(1790812800);

    [Fact]
    public void An_iso8601_reset_time_with_a_z_suffix_is_read_as_utc()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":"2026-10-01T00:00:00Z"}}""");

        var window = Assert.Single(result.Windows);
        Assert.Equal(ExpectedReset, window.ResetsAt);
    }

    [Fact]
    public void An_iso8601_reset_time_with_an_offset_is_converted_to_utc()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":"2026-10-01T02:00:00+02:00"}}""");

        var window = Assert.Single(result.Windows);
        Assert.Equal(ExpectedReset, window.ResetsAt);
    }

    [Fact]
    public void A_unix_seconds_reset_time_is_read_as_before()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":1790812800}}""");

        var window = Assert.Single(result.Windows);
        Assert.Equal(ExpectedReset, window.ResetsAt);
    }

    [Fact]
    public void A_unix_milliseconds_reset_time_is_recognised_by_its_magnitude()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":1790812800000}}""");

        var window = Assert.Single(result.Windows);
        Assert.Equal(ExpectedReset, window.ResetsAt);
    }

    [Fact]
    public void An_implausibly_distant_reset_time_is_dropped_while_the_percentage_still_wins()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"resets_at":4102444800}}""");

        var window = Assert.Single(result.Windows);
        Assert.Equal(42, window.UsedPercent);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Model_scoped_weekly_limits_become_other_windows_with_their_display_name()
    {
        var result = ClaudeUsageParser.Parse("""
            {
              "five_hour": {"utilization": 10, "resets_at": 1790812800},
              "seven_day": {"utilization": 20, "resets_at": 1790812800},
              "limits": [
                {"kind": "weekly_scoped", "percent": 95, "resets_at": 1790812800, "scope": {"model": {"display_name": "Sonnet"}}},
                {"kind": "weekly_scoped", "percent": 30, "resets_at": 1790812800, "scope": {}},
                {"kind": "session", "percent": 50, "resets_at": 1790812800}
              ]
            }
            """);

        Assert.Equal(ClaudeUsageOutcome.Ok, result.Outcome);
        Assert.Equal(3, result.Windows.Count);
        Assert.Single(result.Windows, w => w.Kind == WindowKind.FiveHour);
        Assert.Single(result.Windows, w => w.Kind == WindowKind.Weekly);
        var other = Assert.Single(result.Windows, w => w.Kind == WindowKind.Other);
        Assert.Equal("Sonnet", other.Label);
        Assert.Equal(95, other.UsedPercent);
    }

    [Fact]
    public void Scoped_weekly_limits_are_capped_at_four_in_descending_percent_order()
    {
        var entries = string.Join(",", Enumerable.Range(1, 6)
            .Select(i => $"{{\"kind\":\"weekly_scoped\",\"percent\":{i * 10},\"scope\":{{\"model\":{{\"display_name\":\"Model{i}\"}}}}}}"));
        var result = ClaudeUsageParser.Parse($$"""{"five_hour":{"utilization":1},"limits":[{{entries}}]}""");

        var scoped = result.Windows.Where(w => w.Kind == WindowKind.Other).ToList();
        Assert.Equal(4, scoped.Count);
        Assert.Equal([60, 50, 40, 30], scoped.Select(w => w.UsedPercent));
    }

    [Fact]
    public void A_used_and_limit_pair_becomes_a_requests_allowance()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"used":30,"limit":100}}""");

        var allowance = Assert.Single(result.Windows).Allowance;
        Assert.Equal(new UsageAllowance(30, 100, "Unit.Requests"), allowance);
    }

    [Fact]
    public void A_used_requests_and_request_limit_pair_becomes_a_requests_allowance()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"used_requests":7,"request_limit":50}}""");

        var allowance = Assert.Single(result.Windows).Allowance;
        Assert.Equal(new UsageAllowance(7, 50, "Unit.Requests"), allowance);
    }

    [Fact]
    public void A_used_messages_and_message_limit_pair_becomes_a_messages_allowance()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"used_messages":12,"message_limit":200}}""");

        var allowance = Assert.Single(result.Windows).Allowance;
        Assert.Equal(new UsageAllowance(12, 200, "Unit.Messages"), allowance);
    }

    [Fact]
    public void A_window_with_only_utilization_has_no_allowance()
    {
        var result = ClaudeUsageParser.Parse("""{"five_hour":{"utilization":42}}""");

        Assert.Null(Assert.Single(result.Windows).Allowance);
    }

    [Fact]
    public void A_used_tokens_field_becomes_the_window_s_real_token_count()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"used_tokens":12345}}""");

        Assert.Equal(12345, Assert.Single(result.Windows).Tokens?.TotalTokens);
    }

    [Fact]
    public void A_token_usage_field_becomes_the_window_s_real_token_count()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"token_usage":6789}}""");

        Assert.Equal(6789, Assert.Single(result.Windows).Tokens?.TotalTokens);
    }

    [Fact]
    public void A_total_tokens_field_becomes_the_window_s_real_token_count()
    {
        var result = ClaudeUsageParser.Parse(
            """{"five_hour":{"utilization":42,"total_tokens":555}}""");

        Assert.Equal(555, Assert.Single(result.Windows).Tokens?.TotalTokens);
    }

    [Fact]
    public void A_window_with_no_token_field_at_all_has_no_token_count()
    {
        var result = ClaudeUsageParser.Parse("""{"five_hour":{"utilization":42}}""");

        Assert.Null(Assert.Single(result.Windows).Tokens);
    }
}
