using System.Globalization;
using AiUsage.Models;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class CodexWebUsageParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T20:00:00Z");

    [Theory]
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":9223372036854775807}}}""")]
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":-60}}}""")]
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":0}}}""")]
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":129600000000000}}}""")]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":23,"window_minutes":-5}}}""")]
    public void An_unusable_window_length_reads_as_unknown_and_keeps_the_percentage(string json)
    {
        var window = Assert.Single(CodexWebUsageParser.Parse(json, Now));

        Assert.Equal(23, window.UsedPercent);
        Assert.Null(window.WindowMinutes);
    }

    [Fact]
    public void A_nested_rate_limits_block_becomes_both_windows()
    {
        const string json = """
            {"rate_limits":{"primary":{"used_percent":12.5,"window_minutes":300,"resets_in_seconds":3600},
            "secondary":{"used_percent":64,"window_minutes":10080,"resets_in_seconds":86400}}}
            """;

        var windows = CodexWebUsageParser.Parse(json, Now);

        Assert.Equal(2, windows.Count);
        var fiveHour = windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(12.5, fiveHour.UsedPercent);
        Assert.Equal(300, fiveHour.WindowMinutes);
        Assert.Equal(Now.AddHours(1), fiveHour.ResetsAt);
        Assert.Equal(64, windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public void The_web_apps_rate_limit_windows_become_both_windows()
    {
        const string json = """
            {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
            "primary_window":{"used_percent":23,"limit_window_seconds":18000,"reset_after_seconds":1800,"reset_at":1790000000},
            "secondary_window":{"used_percent":41,"limit_window_seconds":604800,"reset_after_seconds":90000}}}
            """;

        var windows = CodexWebUsageParser.Parse(json, Now);

        Assert.Equal(2, windows.Count);
        var fiveHour = windows.Single(w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(23, fiveHour.UsedPercent);
        Assert.Equal(300, fiveHour.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), fiveHour.ResetsAt);
        var weekly = windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(41, weekly.UsedPercent);
        Assert.Equal(10080, weekly.WindowMinutes);
        Assert.Equal(Now.AddSeconds(90000), weekly.ResetsAt);
    }

    [Fact]
    public void The_same_block_at_the_root_is_read_too()
    {
        const string json = """{"primary":{"used_percent":7},"secondary":{"used_percent":9}}""";

        var windows = CodexWebUsageParser.Parse(json, Now);

        Assert.Equal(2, windows.Count);
        Assert.All(windows, window => Assert.Null(window.ResetsAt));
    }

    [Fact]
    public void An_absolute_reset_instant_is_read_as_it_stands()
    {
        var resetsAt = Now.AddHours(2);
        var windows = CodexWebUsageParser.Parse(BodyWithReset(resetsAt), Now);

        Assert.Equal(resetsAt, Assert.Single(windows).ResetsAt);
    }

    /// <summary>A boundary that has already rolled past describes a window that is over, not one
    /// resetting right now - showing it would make the tile read as stale for no reason.</summary>
    [Fact]
    public void A_reset_instant_already_gone_by_is_dropped()
    {
        Assert.Null(Assert.Single(CodexWebUsageParser.Parse(BodyWithReset(Now.AddMinutes(-1)), Now)).ResetsAt);
    }

    // A millisecond instant used to throw out of the parser and fail the whole read, local numbers
    // included.
    [Fact]
    public void A_reset_instant_in_milliseconds_is_read_and_an_absurd_one_is_dropped()
    {
        var resetsAt = Now.AddHours(2);
        var millis = """{"rate_limits":{"primary":{"used_percent":5,"reset_at":MS}}}"""
            .Replace("MS", resetsAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var absurd = """{"rate_limits":{"primary":{"used_percent":5,"reset_at":9223372036854775807}}}""";

        Assert.Equal(resetsAt, Assert.Single(CodexWebUsageParser.Parse(millis, Now)).ResetsAt);
        Assert.Null(Assert.Single(CodexWebUsageParser.Parse(absurd, Now)).ResetsAt);
    }

    [Fact]
    public void An_absurd_countdown_reads_as_no_reset_instead_of_throwing()
    {
        var body = """{"rate_limits":{"primary":{"used_percent":5,"reset_after_seconds":9223372036854775807}}}""";

        Assert.Null(Assert.Single(CodexWebUsageParser.Parse(body, Now)).ResetsAt);
    }

    private static string BodyWithReset(DateTimeOffset resetsAt) =>
        """{"rate_limits":{"primary":{"used_percent":5,"resets_at":RESET}}}"""
            .Replace("RESET", resetsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void A_block_without_a_percentage_yields_no_window()
    {
        const string json = """{"rate_limits":{"primary":{"window_minutes":300},"secondary":null}}""";

        Assert.Empty(CodexWebUsageParser.Parse(json, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"detail":"Unauthorized"}""")]
    public void An_unknown_shape_yields_no_window_instead_of_a_guess(string json)
    {
        Assert.Empty(CodexWebUsageParser.Parse(json, Now));
    }
}
