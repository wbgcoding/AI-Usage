using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class StatsLimitRowViewModelTests
{
    private static StatsLimitRowViewModel Row(double highest = 10, int reached = 0, string label = "5 hours",
        string reachedText = "never reached", string detail = "avg peak 5 percent") =>
        new(label, reachedText, "10 percent", "5 percent", detail, highest, 5, reached, "five");

    [Fact]
    public void The_constructor_keeps_every_value_and_the_window_key_defaults_to_empty()
    {
        var row = new StatsLimitRowViewModel("Week", "2 times", "100 percent", "60 percent", "detail", 100, 60, 2);

        Assert.Equal("Week", row.Label);
        Assert.Equal("2 times", row.ReachedText);
        Assert.Equal("100 percent", row.HighestText);
        Assert.Equal("60 percent", row.AveragePeakText);
        Assert.Equal("detail", row.DetailText);
        Assert.Equal(100, row.HighestPercent);
        Assert.Equal(60, row.AveragePeakPercent);
        Assert.Equal(2, row.ReachedCount);
        Assert.Equal("", row.WindowKey);
    }

    [Theory]
    [InlineData(0.0, 0, "Ok")]
    [InlineData(79.99, 0, "Ok")]
    [InlineData(80.0, 0, "Warn")]
    [InlineData(99.9, 0, "Warn")]
    [InlineData(100.0, 0, "Warn")]
    [InlineData(0.0, 1, "Crit")]
    [InlineData(50.0, 3, "Crit")]
    [InlineData(100.0, 1, "Crit")]
    public void The_level_is_red_once_reached_amber_from_eighty_percent_else_ok(double highest, int reached, string expected)
    {
        Assert.Equal(expected, Row(highest, reached).Level);
    }

    [Fact]
    public void A_negative_reached_count_does_not_count_as_reached()
    {
        Assert.Equal("Ok", Row(highest: 10, reached: -1).Level);
    }

    [Fact]
    public void An_unknown_highest_value_is_not_a_warning()
    {
        Assert.Equal("Ok", Row(highest: double.NaN).Level);
    }

    [Fact]
    public void The_automation_text_joins_label_then_reached_and_detail()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var text = Row(label: "Weekly", reachedText: "reached twice", detail: "peak 90 percent").AutomationText;

            Assert.Equal("Weekly: reached twice, peak 90 percent", text);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void The_automation_text_skips_an_empty_part_without_a_dangling_separator()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            Assert.Equal("Weekly: reached twice", Row(label: "Weekly", reachedText: "reached twice", detail: "").AutomationText);
            Assert.Equal("Weekly: peak", Row(label: "Weekly", reachedText: "", detail: "peak").AutomationText);
            Assert.Equal("Weekly", Row(label: "Weekly", reachedText: "", detail: "").AutomationText);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void The_automation_text_follows_the_punctuation_of_the_active_language()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("fr");
        try
        {
            Assert.Equal("Semaine : a, b", Row(label: "Semaine", reachedText: "a", detail: "b").AutomationText);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void A_group_keeps_the_provider_and_its_rows()
    {
        var rows = new[] { Row(), Row(label: "Week") };

        var group = new StatsLimitGroupViewModel("claude", "Claude", rows);

        Assert.Equal("claude", group.ProviderId);
        Assert.Equal("Claude", group.ProviderName);
        Assert.Equal(2, group.Rows.Count);
        Assert.Same(rows[1], group.Rows[1]);
    }

    [Fact]
    public void Rows_with_the_same_values_are_equal()
    {
        Assert.Equal(Row(), Row());
        Assert.NotEqual(Row(), Row(highest: 85));
    }
}
