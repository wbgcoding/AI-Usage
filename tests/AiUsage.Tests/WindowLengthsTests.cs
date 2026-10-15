using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>How long a window lasts, and the time marker built on it: every kind of window of every
/// provider has one, and the global switch takes them all away at once.</summary>
public class WindowLengthsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow Window(string label, WindowKind kind, TimeSpan resetIn, int? minutes = null) =>
        new(label, kind, 40, Now + resetIn, minutes);

    [Fact]
    public void A_provider_figure_wins_over_every_default()
    {
        Assert.Equal(123, WindowLengths.MinutesFor(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(1), 123), "codex"));
        Assert.Equal(10080, WindowLengths.MinutesFor(Window("Window_GrokBotWeekly", WindowKind.Other, TimeSpan.FromDays(2), 10080), "cursor"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void The_two_main_windows_last_five_hours_and_a_week(string provider)
    {
        Assert.Equal(300, WindowLengths.MinutesFor(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(1)), provider));
        Assert.Equal(10080, WindowLengths.MinutesFor(Window("Window_Weekly", WindowKind.Weekly, TimeSpan.FromDays(1)), provider));
    }

    [Fact]
    public void A_named_claude_model_window_lasts_a_week_but_an_unnamed_other_window_has_no_length()
    {
        Assert.Equal(10080, WindowLengths.MinutesFor(Window("Opus", WindowKind.Other, TimeSpan.FromDays(1)), "claude"));
        Assert.Null(WindowLengths.MinutesFor(Window("", WindowKind.Other, TimeSpan.FromDays(1)), "claude"));
        Assert.Null(WindowLengths.MinutesFor(Window("Some limit", WindowKind.Other, TimeSpan.FromDays(1)), "codex"));
    }

    [Theory]
    [InlineData("copilot", "Window_CopilotPremium")]
    [InlineData("cursor", "Window_CursorModels")]
    [InlineData("cursor", "Window_OtherModels")]
    [InlineData("cursor", "Window_Month")]
    [InlineData("claude", "Window_Month")]
    public void A_monthly_window_lasts_the_calendar_month_that_ends_at_its_reset(string provider, string label)
    {
        // 1 February to 1 March 2026: a 28 day month.
        var reset = new DateTimeOffset(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local));

        Assert.Equal(28 * 24 * 60, WindowLengths.MinutesFor(new UsageWindow(label, WindowKind.Other, 10, reset, null), provider));
    }

    [Theory]
    [InlineData(2026, 3, 1, 28)] // February 2026
    [InlineData(2028, 3, 1, 29)] // a leap February
    [InlineData(2026, 2, 1, 31)] // January
    public void The_month_length_follows_the_calendar(int year, int month, int day, int days)
    {
        var reset = new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local));

        Assert.Equal(days * 24 * 60, WindowLengths.MonthMinutesEndingAt(reset));
    }

    [Fact]
    public void A_monthly_window_without_a_reset_has_no_length()
    {
        Assert.Null(WindowLengths.MinutesFor(new UsageWindow("Window_CopilotPremium", WindowKind.Other, 10, null, null), "copilot"));
    }

    private static UsageRowViewModel Row(UsageWindow window, string provider) => new(window, Now, TileDensity.Full, null, provider);

    [Fact]
    public void Every_kind_of_window_of_every_provider_shows_the_marker()
    {
        UsageRowViewModel[] rows =
        [
            Row(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2)), "claude"),
            Row(Window("Window_Weekly", WindowKind.Weekly, TimeSpan.FromDays(3)), "claude"),
            Row(Window("Opus", WindowKind.Other, TimeSpan.FromDays(3)), "claude"),
            Row(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2)), "gemini"),
            Row(Window("Window_Weekly", WindowKind.Weekly, TimeSpan.FromDays(3)), "gemini"),
            Row(Window("Window_CopilotPremium", WindowKind.Other, TimeSpan.FromDays(10)), "copilot"),
            Row(Window("Window_CursorModels", WindowKind.Other, TimeSpan.FromDays(10)), "cursor"),
            Row(Window("Window_Month", WindowKind.Other, TimeSpan.FromDays(10)), "cursor"),
            Row(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2), 300), "codex"),
        ];

        Assert.All(rows, row =>
        {
            Assert.NotNull(row.PacePercent);
            Assert.NotEqual("", row.PaceText);
            Assert.Contains(row.PaceText, row.BarTooltipText);
        });
        Assert.Equal(60, rows[0].PacePercent!.Value, 6);
    }

    [Fact]
    public void A_window_the_provider_gives_no_length_for_still_shows_no_marker()
    {
        var row = Row(Window("Some limit", WindowKind.Other, TimeSpan.FromDays(3)), "codex");

        Assert.Null(row.PacePercent);
        Assert.Equal("", row.PaceText);
    }

    [Fact]
    public void Switching_the_marker_off_removes_it_and_its_tooltip_line_and_on_brings_it_back()
    {
        var row = Row(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2)), "claude");
        Assert.NotNull(row.PacePercent);

        row.SetShowPace(false, Now);
        Assert.Null(row.PacePercent);
        Assert.Equal("", row.PaceText);
        Assert.Equal("", row.BarTooltipText);

        row.SetShowPace(true, Now);
        Assert.NotNull(row.PacePercent);
        Assert.NotEqual("", row.BarTooltipText);
    }

    [Fact]
    public void A_row_built_while_the_marker_is_off_stays_without_it_through_updates_and_ticks()
    {
        var window = Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2));
        var row = new UsageRowViewModel(window, Now, TileDensity.Full, null, "claude", showPace: false);

        row.Update(window, Now, TileDensity.Full, null);
        row.RefreshCountdown(Now.AddMinutes(5), TileDensity.Full);

        Assert.Null(row.PacePercent);
    }

    [Fact]
    public void The_tile_switch_changes_every_row_at_once_and_new_rows_follow_it()
    {
        // The switch reads the real clock, so this one runs on it too.
        var now = DateTimeOffset.Now;
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(now), now);
        Assert.All(tile.Rows, row => Assert.NotNull(row.PacePercent));

        tile.ApplyShowPaceMarker(false);
        Assert.All(tile.Rows, row => Assert.Null(row.PacePercent));

        // A later snapshot, including one that rebuilds the rows, keeps the marker off.
        tile.Apply(Snapshot(now), now.AddSeconds(30));
        Assert.All(tile.Rows, row => Assert.Null(row.PacePercent));

        tile.ApplyShowPaceMarker(true);
        Assert.All(tile.Rows, row => Assert.NotNull(row.PacePercent));
    }

    private static ProviderSnapshot Snapshot(DateTimeOffset now) => new(
        "claude",
        [
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 40, now.AddHours(2), null),
            new UsageWindow("Window_Weekly", WindowKind.Weekly, 40, now.AddDays(3), null),
        ],
        "Max", SourceKind.LocalFile, now, now, ProviderStatus.Ok, null);

    [Fact]
    public void The_setting_defaults_on_for_a_file_without_it_and_round_trips()
    {
        Assert.True(SettingsStore.Validate("{}").Settings!.ShowPaceMarker);

        var off = new AppSettings { ShowPaceMarker = false };
        var json = System.Text.Json.JsonSerializer.Serialize(off, SettingsStore.JsonOptions);

        Assert.False(SettingsStore.Validate(json).Settings!.ShowPaceMarker);
    }

    [Fact]
    public void The_window_tokens_still_start_where_each_window_started()
    {
        var reset = Now.AddHours(2);

        Assert.Equal(reset - TimeSpan.FromMinutes(300), UsageRowViewModel.TokenKeyFor(Window("Window_FiveHour", WindowKind.FiveHour, TimeSpan.FromHours(2)), "claude")!.Value.Start);
        Assert.Equal(Now.AddDays(3) - TimeSpan.FromDays(7), UsageRowViewModel.TokenKeyFor(Window("Opus", WindowKind.Other, TimeSpan.FromDays(3)), "claude")!.Value.Start);
        Assert.Equal("opus", UsageRowViewModel.TokenKeyFor(Window("Opus", WindowKind.Other, TimeSpan.FromDays(3)), "claude")!.Value.ModelContains);
        Assert.Null(UsageRowViewModel.TokenKeyFor(Window("Window_Month", WindowKind.Other, TimeSpan.FromDays(3)), "codex"));
        Assert.Null(UsageRowViewModel.TokenKeyFor(Window("Window_CopilotPremium", WindowKind.Other, TimeSpan.FromDays(3)), "copilot"));
    }

    [Fact]
    public void A_reset_instant_wobbling_across_a_minute_boundary_keeps_one_slice()
    {
        var onTheMinute = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, 0, Now.Offset).AddHours(2);
        UsageWindow At(TimeSpan offset) => new("Window_FiveHour", WindowKind.FiveHour, 40, onTheMinute + offset, null);

        var early = UsageRowViewModel.TokenKeyFor(At(TimeSpan.FromMilliseconds(-200)), "claude");
        var late = UsageRowViewModel.TokenKeyFor(At(TimeSpan.FromMilliseconds(200)), "claude");

        Assert.NotNull(early);
        Assert.Equal(early, late);
        Assert.Equal(onTheMinute - TimeSpan.FromMinutes(300), early!.Value.Start);
    }

    [Fact]
    public void A_model_window_with_a_versioned_name_matches_the_model_id_spelling()
    {
        Assert.Equal("sonnet-4-5", UsageRowViewModel.TokenKeyFor(Window("Sonnet 4.5", WindowKind.Other, TimeSpan.FromDays(3)), "claude")!.Value.ModelContains);
    }

    [Fact]
    public void A_reached_claude_limit_of_an_unknown_kind_names_no_model_and_counts_no_slice()
    {
        Assert.Null(UsageRowViewModel.TokenKeyFor(Window("Window_LimitReached", WindowKind.Other, TimeSpan.FromDays(3)), "claude"));
    }
}
