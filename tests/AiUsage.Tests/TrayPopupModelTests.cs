using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The tray popup's content model: the same lines as the shell tooltip, without its length cap.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class TrayPopupModelTests
{
    // Local wall-clock time, so "today" means the same on every machine.
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Local));

    private static string Text(string key) => LocalizationService.Instance[key];

    private static TrayTooltipBuilder.ProviderLine Line(string name, string? id = null, string? note = null) =>
        new(name, 42, 63, note, Now.AddHours(3), Now.AddDays(2).AddHours(4), id);

    [Fact]
    public void Each_provider_is_one_block_with_a_five_hour_and_a_weekly_row_using_the_full_window_labels()
    {
        var model = TrayPopupModel.Build([Line("Claude", "claude"), Line("Codex", "codex")], now: Now);

        Assert.Equal(["Claude", "Codex"], model.Blocks.Select(b => b.Name));
        Assert.All(model.Blocks, b => Assert.Equal([Text("Window.FiveHour"), Text("Window.Weekly")], b.Rows.Select(r => r.Label)));
        Assert.Equal(["TrayPopup.Provider.claude", "TrayPopup.Provider.codex"], model.Blocks.Select(b => b.AutomationId));
    }

    [Fact]
    public void A_line_without_a_provider_id_is_named_by_its_position()
    {
        var model = TrayPopupModel.Build([Line("A"), Line("B")], now: Now);

        Assert.Equal(["TrayPopup.Provider.0", "TrayPopup.Provider.1"], model.Blocks.Select(b => b.AutomationId));
    }

    [Fact]
    public void A_reset_today_shows_the_clock_time_and_a_later_day_the_weekday_and_the_time()
    {
        var model = TrayPopupModel.Build([Line("Claude")], now: Now);
        var rows = model.Blocks[0].Rows;

        var today = TrayTooltipBuilder.ResetTime(Now.AddHours(3), Now);
        var later = TrayTooltipBuilder.ResetTime(Now.AddDays(2).AddHours(4), Now);
        Assert.Equal(LocalizationService.Instance.Format("Tray.Popup.ResetsToday", today), rows[0].Reset);
        Assert.Equal(LocalizationService.Instance.Format("Tray.Popup.ResetsLater", later), rows[1].Reset);
        Assert.Equal(TrayTooltipBuilder.ShortTime(Now.AddHours(3)), today);
        Assert.NotEqual(TrayTooltipBuilder.ShortTime(Now.AddDays(2).AddHours(4)), later);
        Assert.Equal(rows[0].Label + " " + rows[0].Percent + " · " + rows[0].Reset, rows[0].Text);
    }

    [Fact]
    public void A_reset_in_the_past_or_unknown_shows_the_percent_alone()
    {
        var past = new TrayTooltipBuilder.ProviderLine("Claude", 42, 63, null, Now.AddMinutes(-1), null);
        var rows = TrayPopupModel.Build([past], now: Now).Blocks[0].Rows;

        Assert.All(rows, r => Assert.Null(r.Reset));
        Assert.Equal(Text("Window.FiveHour") + " " + StatusTextMap.FormatUsagePercent(42), rows[0].Text);
    }

    [Fact]
    public void An_unknown_percent_shows_a_dash_and_no_time_even_with_a_future_reset()
    {
        var line = new TrayTooltipBuilder.ProviderLine("Claude", null, 63, null, Now.AddHours(3), Now.AddHours(5));
        var rows = TrayPopupModel.Build([line], now: Now).Blocks[0].Rows;

        Assert.Equal("–", rows[0].Percent);
        Assert.Null(rows[0].Reset);
        Assert.NotNull(rows[1].Reset);
    }

    [Fact]
    public void The_note_is_kept_and_an_empty_one_is_dropped()
    {
        var model = TrayPopupModel.Build([Line("Claude", note: "(as of 09:15)"), Line("Codex", note: "")], now: Now);

        Assert.Equal("(as of 09:15)", model.Blocks[0].Note);
        Assert.True(model.Blocks[0].HasNote);
        Assert.Null(model.Blocks[1].Note);
        Assert.False(model.Blocks[1].HasNote);
    }

    [Fact]
    public void The_pause_notice_comes_first_and_only_while_paused()
    {
        var until = Now.AddHours(1);
        var paused = TrayPopupModel.Build([Line("Claude")], until, Now);
        var running = TrayPopupModel.Build([Line("Claude")], null, Now);

        Assert.Equal(TrayTooltipBuilder.PausedLine(until), paused.PausedLine);
        Assert.StartsWith(TrayTooltipBuilder.PausedLine(until) + "\n", paused.PlainText);
        Assert.Null(running.PausedLine);
        Assert.False(running.HasPausedLine);
    }

    [Fact]
    public void Five_providers_all_keep_their_reset_times_where_the_shell_tooltip_has_to_drop_them()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Line("Provider" + i, "p" + i)).ToList();

        var model = TrayPopupModel.Build(lines, Now.AddHours(1), Now);

        Assert.Equal(5, model.Blocks.Count);
        Assert.All(model.Blocks.SelectMany(b => b.Rows), r => Assert.NotNull(r.Reset));
        Assert.True(model.PlainText.Length > TrayTooltipBuilder.MaxLength);
    }

    [Fact]
    public void Nothing_to_show_is_empty()
    {
        var model = TrayPopupModel.Build([], now: Now);

        Assert.True(model.IsEmpty);
        Assert.False(TrayPopupModel.Build([Line("Claude")], now: Now).IsEmpty);
    }

    [Fact]
    public void The_reset_wording_follows_the_language()
    {
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            var en = TrayPopupModel.Build([Line("Claude")], now: Now).Blocks[0].Rows[0];
            Assert.Equal("5 hours", en.Label);
            Assert.StartsWith("back at ", en.Reset);

            LocalizationService.Instance.SetLanguage("de");
            var de = TrayPopupModel.Build([Line("Claude")], now: Now).Blocks[0].Rows[0];
            Assert.Equal("5 Stunden", de.Label);
            Assert.StartsWith("zurück um ", de.Reset);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }
}
