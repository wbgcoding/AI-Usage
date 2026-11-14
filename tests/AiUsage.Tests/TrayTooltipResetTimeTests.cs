using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class TrayTooltipResetTimeTests
{
    // Local wall-clock time, so "today" means the same on every machine.
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Local));

    private static TrayTooltipBuilder.ProviderLine Line(string name, string? note = null, bool times = true) =>
        new(name, 42, 63, note, times ? Now.AddHours(3) : null, times ? Now.AddDays(2).AddHours(4) : null);

    [Fact]
    public void A_reset_today_shows_the_clock_time_and_a_later_day_the_weekday_first()
    {
        var today = Now.AddHours(3);
        var later = Now.AddDays(2).AddHours(4);

        Assert.Equal(TrayTooltipBuilder.ShortTime(today), TrayTooltipBuilder.ResetTime(today, Now));
        var text = TrayTooltipBuilder.ResetTime(later, Now);
        Assert.EndsWith(" " + TrayTooltipBuilder.ShortTime(later), text);
        Assert.True(text.Length > TrayTooltipBuilder.ShortTime(later).Length + 1);
    }

    [Fact]
    public void Two_providers_fit_with_their_reset_times()
    {
        var text = TrayTooltipBuilder.Build([Line("Claude"), Line("Codex")], now: Now);

        var today = TrayTooltipBuilder.ShortTime(Now.AddHours(3));
        var week = TrayTooltipBuilder.ResetTime(Now.AddDays(2).AddHours(4), Now);
        Assert.Equal(2, text.Split('\n').Length);
        Assert.All(text.Split('\n'), line =>
        {
            Assert.Contains($"42 % ({today})", line);
            Assert.Contains($"63 % ({week})", line);
        });
        Assert.True(text.Length <= TrayTooltipBuilder.MaxLength);
    }

    [Fact]
    public void Many_providers_drop_the_times_before_any_name_is_shortened()
    {
        var withTimes = Enumerable.Range(0, 3).Select(i => Line("Provider" + i)).ToArray();
        var without = Enumerable.Range(0, 3).Select(i => Line("Provider" + i, times: false)).ToArray();

        var text = TrayTooltipBuilder.Build(withTimes, now: Now);

        Assert.DoesNotContain("(", text);
        Assert.Contains("Provider0", text);
        Assert.Equal(TrayTooltipBuilder.Build(without, now: Now), text);
    }

    [Fact]
    public void The_notes_stay_while_dropping_the_times_is_enough()
    {
        var withTimes = new[] { Line("Provider0", "Anmelden"), Line("Provider1"), Line("Provider2") };

        var text = TrayTooltipBuilder.Build(withTimes, now: Now);

        Assert.DoesNotContain("(", text);
        Assert.Contains("Anmelden", text);
    }

    [Fact]
    public void Even_more_providers_shorten_names_without_times_and_never_pass_the_limit()
    {
        var lines = Enumerable.Range(0, 8).Select(i => Line("Provider" + i, i == 0 ? "Anmelden" : null)).ToArray();

        var text = TrayTooltipBuilder.Build(lines, now: Now);

        Assert.DoesNotContain("(", text);
        Assert.Contains("Pro1", text);
        Assert.True(text.Length <= TrayTooltipBuilder.MaxLength);
    }

    [Fact]
    public void The_result_is_never_over_the_limit_for_any_number_of_providers_or_a_notice()
    {
        for (var count = 1; count <= 14; count++)
        {
            var lines = Enumerable.Range(0, count).Select(i => Line("Provider" + i, i % 2 == 0 ? "Anmelden" : null)).ToArray();

            Assert.True(TrayTooltipBuilder.Build(lines, now: Now).Length <= TrayTooltipBuilder.MaxLength, $"{count} providers");
            Assert.True(TrayTooltipBuilder.Build(lines, "Pausiert bis 14:30", Now).Length <= TrayTooltipBuilder.MaxLength, $"{count} providers with a notice");
        }
    }

    [Fact]
    public void A_missing_percent_or_a_reset_in_the_past_shows_no_time()
    {
        var line = new TrayTooltipBuilder.ProviderLine("Claude", null, 63, null, Now.AddHours(3), Now.AddHours(-1));

        var text = TrayTooltipBuilder.Build([line], now: Now);

        Assert.DoesNotContain("(", text);
        Assert.DoesNotContain("–", text);
    }
}
