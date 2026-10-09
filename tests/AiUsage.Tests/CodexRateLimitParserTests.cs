using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class CodexRateLimitParserTests
{
    private const string ValidLine =
        """{"timestamp":"2026-09-01T10:00:00Z","ordinal":2,"type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":{"limit_id":"codex","primary":{"used_percent":2.0,"window_minutes":300,"resets_at":1788642834},"secondary":{"used_percent":88.0,"window_minutes":10080,"resets_at":1788860573},"plan_type":"plus"}}}""";

    /// <summary>The shape a short session writes: the event is complete, both windows are null.</summary>
    private const string BothWindowsNullLine =
        """{"timestamp":"2026-09-01T09:30:00Z","ordinal":2,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1290},"last_token_usage":{},"model_context_window":258400},"rate_limits":{"limit_id":"premium","limit_name":null,"primary":null,"secondary":null,"credits":{"has_credits":false,"unlimited":false,"balance":"0"},"individual_limit":null,"spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":null}}}""";

    /// <summary>Same shape, but the weekly window carries a number - that line is usable.</summary>
    private const string WeeklyOnlyLine =
        """{"timestamp":"2026-09-01T09:45:00Z","ordinal":3,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1290},"last_token_usage":{},"model_context_window":258400},"rate_limits":{"limit_id":"premium","limit_name":null,"primary":null,"secondary":{"used_percent":41.0,"window_minutes":10080,"resets_at":1788860573},"credits":null,"individual_limit":null,"spend_control_reached":null,"plan_type":"plus","rate_limit_reached_type":null}}}""";

    [Fact]
    public void TryParse_reads_both_windows_and_the_plan()
    {
        var ok = CodexRateLimitParser.TryParse(ValidLine, out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Equal(2.0, result!.PrimaryUsedPercent);
        Assert.Equal(300, result.PrimaryWindowMinutes);
        Assert.Equal(88.0, result.SecondaryUsedPercent);
        Assert.Equal(10080, result.SecondaryWindowMinutes);
        Assert.Equal("plus", result.PlanType);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), result.Timestamp);
    }

    [Fact]
    public void TryParse_skips_a_line_whose_two_windows_are_both_null()
    {
        var ok = CodexRateLimitParser.TryParse(BothWindowsNullLine, out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_reads_the_weekly_window_alone_when_the_five_hour_one_is_null()
    {
        var ok = CodexRateLimitParser.TryParse(WeeklyOnlyLine, out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Null(result!.PrimaryUsedPercent);
        Assert.Null(result.PrimaryWindowMinutes);
        Assert.Null(result.PrimaryResetsAt);
        Assert.Equal(41.0, result.SecondaryUsedPercent);
        Assert.Equal(10080, result.SecondaryWindowMinutes);
        Assert.Equal("plus", result.PlanType);
        Assert.Equal(1290L, result.TotalTokens);
    }

    [Fact]
    public void TryParse_nulls_out_a_total_token_count_and_reset_time_that_do_not_fit_their_types()
    {
        const string line =
            """{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":99999999999999999999}},"rate_limits":{"primary":{"used_percent":2.0,"window_minutes":300,"resets_at":99999999999999},"secondary":{"used_percent":88.0,"window_minutes":10080,"resets_at":99999999999999}}}}""";

        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.True(ok); // the rest of the line still matches - same "not measured" treatment as a missing field
        Assert.NotNull(result);
        Assert.Null(result!.TotalTokens);
        Assert.Null(result.PrimaryResetsAt);
        Assert.Null(result.SecondaryResetsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"event_msg","payload":{"type":"other_event"}}""")]
    [InlineData("""{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count"}}""")]
    [InlineData("""{"timestamp":"not-a-date","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{},"secondary":{}}}}""")]
    public void TryParse_rejects_anything_that_is_not_a_complete_rate_limit_line(string line)
    {
        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_drops_an_implausibly_distant_reset_time_while_the_percentage_still_wins()
    {
        // 4102444800 is 2100-01-01T00:00:00Z: a representable DateTimeOffset, but far past what a
        // real rate-limit reset can be - the same bound ClaudeUsageParser already applies.
        const string line =
            """{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":2.0,"window_minutes":300,"resets_at":4102444800},"secondary":{"used_percent":88.0,"window_minutes":10080,"resets_at":4102444800}}}}""";

        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Equal(2.0, result!.PrimaryUsedPercent);
        Assert.Null(result.PrimaryResetsAt);
        Assert.Equal(88.0, result.SecondaryUsedPercent);
        Assert.Null(result.SecondaryResetsAt);
    }

    [Fact]
    public void TryParse_keeps_a_plausible_reset_time_unchanged()
    {
        const string line =
            """{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":2.0,"window_minutes":300,"resets_at":1788642834}}}}""";

        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788642834), result!.PrimaryResetsAt);
    }

    [Fact]
    public void TryParse_reads_a_timestamp_without_an_offset_as_UTC()
    {
        var line = ValidLine.Replace("2026-09-01T10:00:00Z", "2026-09-01T10:00:00");

        Assert.True(CodexRateLimitParser.TryParse(line, out var result));

        Assert.Equal(TimeSpan.Zero, result!.Timestamp.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), result.Timestamp);
    }
}
