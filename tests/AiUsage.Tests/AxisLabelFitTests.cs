using AiUsage.Views.Controls;

namespace AiUsage.Tests;

/// <summary>The pure decisions behind the stats charts' label fitting at a narrow window: which hour
/// axis labels stay, and how wide the name column of a plain bar row becomes.</summary>
public class AxisLabelFitTests
{
    [Fact]
    public void NonOverlappingLabels_keeps_every_label_that_has_room()
    {
        var candidates = new List<(int Index, double Left, double Width)> { (0, 0, 30), (6, 60, 30), (12, 120, 30), (18, 180, 30) };

        Assert.Equal([0, 6, 12, 18], StatsBarChart.NonOverlappingLabels(candidates, [0, 12], 6));
    }

    [Fact]
    public void NonOverlappingLabels_drops_the_optional_labels_that_touch_the_required_ones()
    {
        // 6 and 18 sit right against 0 and 12; the required two stay, the others go.
        var candidates = new List<(int Index, double Left, double Width)> { (0, 0, 30), (6, 28, 30), (12, 56, 30), (18, 84, 30) };

        Assert.Equal([0, 12], StatsBarChart.NonOverlappingLabels(candidates, [0, 12], 6));
    }

    [Fact]
    public void NonOverlappingLabels_keeps_a_required_label_even_when_two_required_ones_touch()
    {
        var candidates = new List<(int Index, double Left, double Width)> { (0, 0, 30), (12, 20, 30) };

        Assert.Equal([0, 12], StatsBarChart.NonOverlappingLabels(candidates, [0, 12], 6));
    }

    [Fact]
    public void NonOverlappingLabels_without_required_labels_skips_each_one_that_reaches_the_previous()
    {
        var candidates = new List<(int Index, double Left, double Width)> { (0, 0, 30), (1, 34, 30), (2, 70, 30) };

        Assert.Equal([0, 2], StatsBarChart.NonOverlappingLabels(candidates, [], 6));
    }

    [Fact]
    public void NameColumnWidthFor_keeps_the_fixed_share_for_short_names()
    {
        Assert.Equal(70, StatsHorizontalBarChart.NameColumnWidthFor(200, 20));
    }

    [Fact]
    public void NameColumnWidthFor_widens_toward_the_widest_name()
    {
        var width = StatsHorizontalBarChart.NameColumnWidthFor(215, 90);

        Assert.True(width >= 90);
    }

    [Fact]
    public void NameColumnWidthFor_leaves_the_bar_a_minimum_width()
    {
        var width = StatsHorizontalBarChart.NameColumnWidthFor(215, 500);

        // 56 for the figure, 6 gap and 4 for the bar stay free.
        Assert.Equal(215 - 56 - 6 - 4, width);
    }

    [Fact]
    public void NameFontSizeFor_keeps_the_regular_size_while_the_names_fit()
    {
        Assert.Equal(11, StatsHorizontalBarChart.NameFontSizeFor(50, 60));
    }

    [Fact]
    public void NameFontSizeFor_steps_down_in_proportion_and_stops_at_a_floor()
    {
        Assert.Equal(11 * 50.0 / 60, StatsHorizontalBarChart.NameFontSizeFor(60, 50), 6);
        Assert.Equal(8.5, StatsHorizontalBarChart.NameFontSizeFor(200, 50));
    }

    [Fact]
    public void RequiredLabelsTouch_is_true_only_when_two_required_labels_come_closer_than_the_gap()
    {
        var apart = new List<(int Index, double Left, double Width)> { (0, 0, 30), (6, 40, 30), (12, 100, 30) };
        var touching = new List<(int Index, double Left, double Width)> { (0, 0, 30), (6, 40, 30), (12, 33, 30) };

        Assert.False(StatsBarChart.RequiredLabelsTouch(apart, [0, 12], 6));
        Assert.True(StatsBarChart.RequiredLabelsTouch(touching, [0, 12], 6));
    }
}
