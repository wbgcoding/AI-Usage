using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Coverage for the unit word appended to the self-drawn statistics charts' own hover
/// tooltip and center text - the own tests for the rest of each control's geometry live in
/// <c>StatsWindowTests.cs</c>.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsChartTests
{
    [Fact]
    public void A_long_project_name_stays_on_one_line_instead_of_wrapping_into_the_path_line()
    {
        var face = new Typeface("Segoe UI");
        var oneLine = StatsHorizontalBarChart.CreateRichNameText("a", 60, CultureInfo.InvariantCulture, face, Brushes.Black, 1);

        var long_ = StatsHorizontalBarChart.CreateRichNameText(
            "a-very-long-project-folder-name with several words to wrap", 60, CultureInfo.InvariantCulture, face, Brushes.Black, 1);

        Assert.Equal(oneLine.Height, long_.Height, 3);
    }

    [Fact]
    public void BuildTooltipLines_appends_the_token_word_to_every_value_line_when_set()
    {
        var bar = new StatsBarChart.Bar("2026-01-01", [100, 50]);

        var lines = StatsBarChart.BuildTooltipLines(bar, ["Claude", "Codex"], "tokens");

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain("tokens", lines[0]); // the label/date line never carries the unit
        Assert.EndsWith(" tokens", lines[1]);
        Assert.EndsWith(" tokens", lines[2]);
    }

    [Fact]
    public void BuildTooltipLines_leaves_value_lines_unchanged_without_a_token_word()
    {
        var bar = new StatsBarChart.Bar("2026-01-01", [100, 50]);

        var lines = StatsBarChart.BuildTooltipLines(bar, ["Claude", "Codex"], "");

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain("tokens", lines[1]);
        Assert.DoesNotContain("tokens", lines[2]);
    }

    [Fact]
    public void BuildTooltipLines_appends_the_token_word_to_a_single_total_line()
    {
        var bar = new StatsBarChart.Bar("modelA", [42]);

        var lines = StatsBarChart.BuildTooltipLines(bar, [], "Token");

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain("Token", lines[0]);
        Assert.EndsWith(" Token", lines[1]);
    }

    [Fact]
    public void BuildTooltipHeaderLineAddsTheWeekdayToADayLabel()
    {
        var header = StatsBarChart.BuildTooltipHeaderLine("2026-09-26", new CultureInfo("de-DE"));

        Assert.Equal("26.09.2026 Sa", header);
    }

    [Fact]
    public void BuildTooltipHeaderLineTurnsAnIsoWeekLabelIntoItsDateRange()
    {
        var monday = new DateOnly(2026, 9, 21); // a known Monday
        var label = AiUsage.Stats.StatsAggregator.IsoWeekLabel(monday);

        var header = StatsBarChart.BuildTooltipHeaderLine(label, new CultureInfo("de-DE"));

        Assert.Equal("21.-27.09.2026", header);
    }

    [Fact]
    public void FormatAxisLabelTurnsAnIsoWeekLabelIntoItsOwnDateRangeToo()
    {
        var monday = new DateOnly(2026, 9, 21);
        var label = AiUsage.Stats.StatsAggregator.IsoWeekLabel(monday);

        var axisText = StatsBarChart.FormatAxisLabel(label, CultureInfo.CurrentCulture);
        var expected = AiUsage.Stats.StatsTooltipDateFormatter.FormatRange(
            monday, monday.AddDays(6), CultureInfo.CurrentCulture);

        Assert.Equal(expected, axisText);
    }

    [Fact]
    public void BuildTooltipHeaderLinePassesThroughALabelThatIsNeitherADayNorAWeek()
    {
        Assert.Equal("modelA", StatsBarChart.BuildTooltipHeaderLine("modelA", CultureInfo.CurrentCulture));
        Assert.Equal("Mo", StatsBarChart.BuildTooltipHeaderLine("Mo", CultureInfo.CurrentCulture));
    }

    // The acceptance criterion here: the tooltip names the row by its own full, untrimmed path - not
    // the shortened label the bar itself draws - plus its total, share and activity range.
    [Fact]
    public void StatsHorizontalBarChart_BuildTooltipLines_names_the_full_path_total_share_and_activity_range()
    {
        var row = new StatsProjectRow("ai-usage", @"C:\code\ai-usage", 1234, 42.5, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 5));

        var lines = StatsHorizontalBarChart.BuildTooltipLines(row, "tokens", "Total", "Active", new CultureInfo("de-DE"));

        Assert.Equal(@"C:\code\ai-usage", lines[0]);
        Assert.Equal("Total: 1.234 tokens", lines[1]);
        Assert.Equal("42,5 %", lines[2]); // German decimal separator and percent spacing
        Assert.Equal("Active: 01.-05.01.2026", lines[3]);
    }
}
