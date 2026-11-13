using System.Text.Json;
using AiUsage.Models;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

/// <summary>Which bar a Codex rate-limit window lands on, how a reported length is judged and which
/// notes a rate-limit container produces for a shape the reads were not written for.</summary>
public class CodexWindowKindsTests
{
    private static readonly string[] Known = ["primary_window", "secondary_window"];

    private static List<string> Notes(string json, string[]? known = null)
    {
        using var document = JsonDocument.Parse(json);
        var notes = new List<string>();
        CodexWindowKinds.CollectNotes(document.RootElement, "rate_limit", known ?? Known, notes);
        return notes;
    }

    [Theory]
    [InlineData(300, WindowKind.Other, WindowKind.FiveHour)]
    [InlineData(1439, WindowKind.Weekly, WindowKind.FiveHour)]
    [InlineData(1440, WindowKind.FiveHour, WindowKind.Weekly)]
    [InlineData(10080, WindowKind.FiveHour, WindowKind.Weekly)]
    [InlineData(1, WindowKind.Weekly, WindowKind.FiveHour)]
    [InlineData(int.MaxValue, WindowKind.FiveHour, WindowKind.Weekly)]
    public void A_known_length_decides_the_kind_whatever_the_position(int minutes, WindowKind byPosition, WindowKind expected) =>
        Assert.Equal(expected, CodexWindowKinds.KindFor(minutes, byPosition));

    [Theory]
    [InlineData(null, WindowKind.Weekly)]
    [InlineData(0, WindowKind.FiveHour)]
    [InlineData(-5, WindowKind.Weekly)]
    public void A_missing_or_unusable_length_falls_back_to_the_position(int? minutes, WindowKind byPosition) =>
        Assert.Equal(byPosition, CodexWindowKinds.KindFor(minutes, byPosition));

    [Fact]
    public void A_later_window_that_resolves_to_the_same_kind_keeps_its_position()
    {
        // Two short windows: the second one would claim the five-hour slot again, so it stays where it is.
        Assert.Equal(WindowKind.Weekly, CodexWindowKinds.KindFor(300, WindowKind.Weekly, WindowKind.FiveHour));
    }

    [Fact]
    public void A_later_window_with_a_different_kind_keeps_the_kind_its_length_gives()
    {
        Assert.Equal(WindowKind.Weekly, CodexWindowKinds.KindFor(10080, WindowKind.FiveHour, WindowKind.FiveHour));
        Assert.Equal(WindowKind.FiveHour, CodexWindowKinds.KindFor(300, WindowKind.FiveHour, null));
    }

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(300L, 300)]
    [InlineData(576000L, 576000)]
    public void A_plausible_length_is_kept(long minutes, int expected) =>
        Assert.Equal(expected, CodexWindowKinds.PlausibleMinutesOrNull(minutes));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(576001L)]
    [InlineData(long.MaxValue)]
    public void A_missing_or_absurd_length_is_unknown(long? minutes) =>
        Assert.Null(CodexWindowKinds.PlausibleMinutesOrNull(minutes));

    [Fact]
    public void Labels_are_the_resource_keys_of_the_two_bars()
    {
        Assert.Equal("Window_Weekly", CodexWindowKinds.LabelFor(WindowKind.Weekly));
        Assert.Equal("Window_FiveHour", CodexWindowKinds.LabelFor(WindowKind.FiveHour));
        Assert.Equal("Window_FiveHour", CodexWindowKinds.LabelFor(WindowKind.Other));
    }

    [Fact]
    public void The_usual_shape_produces_no_notes() =>
        Assert.Empty(Notes("""
        {
          "primary_window": { "used_percent": 12, "limit_window_seconds": 18000 },
          "secondary_window": { "used_percent": 3, "window_minutes": 10080 }
        }
        """));

    [Fact]
    public void A_container_that_is_not_an_object_says_nothing()
    {
        Assert.Empty(Notes("[1, 2]"));
        Assert.Empty(Notes("null"));
        Assert.Empty(Notes("\"text\""));
        Assert.Empty(Notes("{}"));
    }

    [Fact]
    public void A_null_or_scalar_window_is_the_normal_no_number_shape()
    {
        Assert.Empty(Notes("""{ "primary_window": null, "secondary_window": 4, "other": "x" }"""));
    }

    [Fact]
    public void An_unknown_window_with_a_number_is_reported_by_name()
    {
        var note = Assert.Single(Notes("""{ "tertiary_window": { "used_percent": 5 } }"""));
        Assert.Contains("tertiary_window", note);
        Assert.Contains("'rate_limit'", note);
        Assert.Contains("not shown", note);
    }

    [Fact]
    public void An_unknown_window_without_a_number_stays_quiet()
    {
        Assert.Empty(Notes("""{ "tertiary_window": { "used_percent": "high" } }"""));
        Assert.Empty(Notes("""{ "tertiary_window": { "reset": 5 } }"""));
    }

    [Fact]
    public void A_known_window_without_used_percent_lists_its_keys()
    {
        var note = Assert.Single(Notes("""{ "primary_window": { "reset_after": 5, "window_minutes": 300 } }"""));
        Assert.Contains("primary_window", note);
        Assert.Contains("no used_percent", note);
        Assert.Contains("reset_after", note);
        Assert.Contains("window_minutes", note);
    }

    [Fact]
    public void A_length_that_is_neither_five_hours_nor_a_week_is_noted_in_minutes()
    {
        var note = Assert.Single(Notes("""{ "primary_window": { "used_percent": 1, "window_minutes": 600 } }"""));
        Assert.Contains("600 minutes", note);
    }

    [Fact]
    public void The_length_in_seconds_is_used_when_minutes_are_missing()
    {
        var note = Assert.Single(Notes("""{ "primary_window": { "used_percent": 1, "limit_window_seconds": 36000 } }"""));
        Assert.Contains("600 minutes", note);
        Assert.Empty(Notes("""{ "primary_window": { "used_percent": 1, "limit_window_seconds": 18000 } }"""));
    }

    [Fact]
    public void Minutes_take_precedence_over_seconds()
    {
        Assert.Empty(Notes("""{ "primary_window": { "used_percent": 1, "window_minutes": 300, "limit_window_seconds": 36000 } }"""));
    }

    [Fact]
    public void An_absurd_length_is_not_noted()
    {
        Assert.Empty(Notes("""{ "primary_window": { "used_percent": 1, "window_minutes": 99999999 } }"""));
        Assert.Empty(Notes("""{ "primary_window": { "used_percent": 1, "window_minutes": 0 } }"""));
    }

    [Fact]
    public void Several_findings_come_out_one_note_each()
    {
        var notes = Notes("""
        {
          "primary_window": { "used_percent": 1, "window_minutes": 45 },
          "secondary_window": { "limit": 5 },
          "extra": { "used_percent": 2 }
        }
        """);

        Assert.Equal(3, notes.Count);
    }

    [Fact]
    public void Odd_characters_in_a_window_name_never_reach_the_note()
    {
        var note = Assert.Single(Notes("""{ "pri<m>ary\n": { "used_percent": 5 } }"""));
        Assert.DoesNotContain("<", note);
        Assert.DoesNotContain("\n", note);
    }
}
