using System.Globalization;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

public class FractionBarTests
{
    [Theory]
    [InlineData(0.0, 200.0, 0.0)]
    [InlineData(-1.0, 200.0, 0.0)]
    [InlineData(double.NaN, 200.0, 0.0)]
    [InlineData(0.5, 200.0, 100.0)]
    [InlineData(1.0, 200.0, 200.0)]
    [InlineData(3.0, 200.0, 200.0)]
    [InlineData(0.001, 200.0, 3.0)]
    [InlineData(0.5, 0.0, 0.0)]
    public void The_fill_is_the_fraction_of_the_track_with_a_floor_so_a_small_value_stays_visible(double fraction, double track, double expected) =>
        Assert.Equal(expected, FractionBar.FilledWidth(fraction, track), 6);

    [Theory]
    [InlineData(2, 800.0, null, 2)]
    [InlineData(2, 300.0, "210", 1)]
    [InlineData(2, 450.0, "210", 2)]
    [InlineData(2, 250.0, null, 2)]
    [InlineData(2, 400.0, "abc", 2)]
    public void The_card_columns_follow_the_width_and_the_optional_minimum(int cards, double width, string? minimum, int expected)
    {
        var columns = new FigureCardColumnsConverter().Convert([cards, width], typeof(int), minimum, CultureInfo.InvariantCulture);
        Assert.Equal(expected, columns);
    }

    [Theory]
    [InlineData(500.0, "420", true)]
    [InlineData(420.0, "420", true)]
    [InlineData(419.0, "420", false)]
    [InlineData(0.0, "420", true)]
    [InlineData(double.NaN, "420", true)]
    [InlineData(300.0, null, false)]
    public void The_wide_arrangement_starts_at_the_given_width(double width, string? minimum, bool expected) =>
        Assert.Equal(expected, new AtLeastWidthConverter().Convert(width, typeof(bool), minimum, CultureInfo.InvariantCulture));
}
