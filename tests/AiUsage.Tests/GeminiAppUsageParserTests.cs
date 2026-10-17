using AiUsage.Models;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

/// <summary>
/// The Gemini app's usage answer is positional JSON (<c>[2, entries, false]</c>): kind 1 is the
/// five-hour window, kind 2 the weekly one. A real payload yields both windows; anything the parser
/// does not recognise is dropped and noted instead of shown as a guessed number.
/// </summary>
public class GeminiAppUsageParserTests
{
    private const string RealPayload = "[2,[[2181,0.09,1,[[1792246090,287070000]]],[45607,0.05737659,2,[[1792652890,287184000]]]],false]";

    [Fact]
    public void The_real_payload_becomes_both_windows()
    {
        var windows = GeminiAppUsageParser.Parse(RealPayload);

        Assert.Equal(2, windows.Count);
        var fiveHour = windows.Single(w => w.Kind == WindowKind.FiveHour);
        var weekly = windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal("Window_FiveHour", fiveHour.Label);
        Assert.Equal("Window_Weekly", weekly.Label);
        Assert.Equal(9, fiveHour.UsedPercent, precision: 6);
        Assert.Equal(5.737659, weekly.UsedPercent, precision: 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792246090), fiveHour.ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792652890), weekly.ResetsAt);
        Assert.Null(fiveHour.WindowMinutes);
        Assert.Empty(GeminiAppUsageParser.ChangeNotes(RealPayload));
    }

    [Fact]
    public void The_entry_order_does_not_matter()
    {
        var windows = GeminiAppUsageParser.Parse("[2,[[45607,0.5,2,[[1792652890,0]]],[2181,0.25,1,[[1792246090,0]]]],false]");

        Assert.Equal(25, windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent, precision: 6);
        Assert.Equal(50, windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent, precision: 6);
    }

    [Fact]
    public void An_unused_window_with_a_null_fraction_is_zero_percent()
    {
        const string payload = "[2,[[2181,null,1,[[1792246090,0]]]],false]";

        var window = Assert.Single(GeminiAppUsageParser.Parse(payload));
        Assert.Equal(WindowKind.FiveHour, window.Kind);
        Assert.Equal(0, window.UsedPercent);
        Assert.Empty(GeminiAppUsageParser.ChangeNotes(payload));
    }

    [Fact]
    public void A_window_without_a_reset_is_still_shown()
    {
        var window = Assert.Single(GeminiAppUsageParser.Parse("[2,[[2181,0.4,1]],false]"));

        Assert.Equal(40, window.UsedPercent, precision: 6);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void A_reset_far_from_now_is_dropped_but_the_window_stays()
    {
        var window = Assert.Single(GeminiAppUsageParser.Parse("[2,[[2181,0.4,1,[[4102444800,0]]]],false]"));

        Assert.Null(window.ResetsAt);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("\"0.5\"")]
    [InlineData("true")]
    [InlineData("[]")]
    public void A_fraction_that_is_no_number_between_0_and_1_drops_the_entry(string fraction)
    {
        var payload = $"[2,[[2181,{fraction},1,[[1792246090,0]]],[45607,0.5,2,[[1792652890,0]]]],false]";

        var window = Assert.Single(GeminiAppUsageParser.Parse(payload));
        Assert.Equal(WindowKind.Weekly, window.Kind);
        Assert.Contains("used fraction that is no number between 0 and 1", Assert.Single(GeminiAppUsageParser.ChangeNotes(payload)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bounds_0_and_1_are_valid()
    {
        var windows = GeminiAppUsageParser.Parse("[2,[[1,0,1],[2,1,2]],false]");

        Assert.Equal(0, windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(100, windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public void An_unknown_kind_is_dropped_and_noted_by_number()
    {
        const string payload = "[2,[[2181,0.09,1,[[1792246090,0]]],[7,0.3,3,[[1792652890,0]]]],false]";

        var window = Assert.Single(GeminiAppUsageParser.Parse(payload));
        Assert.Equal(WindowKind.FiveHour, window.Kind);
        Assert.Equal("the usage answer names an unknown window kind '3', which is not shown.", Assert.Single(GeminiAppUsageParser.ChangeNotes(payload)));
    }

    [Fact]
    public void A_kind_that_is_no_whole_number_is_dropped_and_noted()
    {
        const string payload = "[2,[[2181,0.09,\"daily\"]],false]";

        Assert.Empty(GeminiAppUsageParser.Parse(payload));
        var notes = GeminiAppUsageParser.ChangeNotes(payload);
        Assert.Contains(notes, note => note.Contains("unknown window kind 'daily'", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("no five-hour or weekly window", StringComparison.Ordinal));
    }

    [Fact]
    public void A_kind_named_twice_shows_the_first_only_and_is_noted()
    {
        const string payload = "[2,[[1,0.1,1],[2,0.9,1]],false]";

        Assert.Equal(10, Assert.Single(GeminiAppUsageParser.Parse(payload)).UsedPercent, precision: 6);
        Assert.Contains("more than once", Assert.Single(GeminiAppUsageParser.ChangeNotes(payload)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_good_shape_without_any_window_is_noted()
    {
        const string payload = "[2,[],false]";

        Assert.Empty(GeminiAppUsageParser.Parse(payload));
        Assert.Contains("no five-hour or weekly window", Assert.Single(GeminiAppUsageParser.ChangeNotes(payload)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[2]")]
    [InlineData("[2,null,false]")]
    [InlineData("[2,\"x\",false]")]
    public void An_unknown_shape_yields_no_window_and_one_note(string payload)
    {
        Assert.Empty(GeminiAppUsageParser.Parse(payload));
        Assert.Equal("the usage answer has an unknown shape, so no usage window is shown.", Assert.Single(GeminiAppUsageParser.ChangeNotes(payload)));
    }

    [Fact]
    public void A_non_array_entry_is_skipped_without_failing_the_rest()
    {
        var window = Assert.Single(GeminiAppUsageParser.Parse("[2,[5,null,[2181,0.2,1]],false]"));

        Assert.Equal(20, window.UsedPercent, precision: 6);
    }
}
