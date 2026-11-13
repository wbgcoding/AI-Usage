using System.Globalization;
using System.Windows;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class TileWidthToVisibilityConverterTests
{
    private readonly TileWidthToVisibilityConverter _converter = new();

    private object Convert(object? value) =>
        _converter.Convert(value!, typeof(Visibility), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(300.0)]
    [InlineData(300.5)]
    [InlineData(1000.0)]
    [InlineData(double.PositiveInfinity)]
    public void A_tile_at_or_above_the_threshold_shows_the_clock(double width)
    {
        Assert.Equal(Visibility.Visible, Convert(width));
    }

    [Theory]
    [InlineData(299.99)]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    public void A_narrower_tile_collapses_the_clock(double width)
    {
        Assert.Equal(Visibility.Collapsed, Convert(width));
    }

    [Fact]
    public void An_unset_value_collapses_the_clock()
    {
        Assert.Equal(Visibility.Collapsed, Convert(null));
        Assert.Equal(Visibility.Collapsed, Convert(DependencyProperty.UnsetValue));
    }

    [Theory]
    [InlineData(400)]
    [InlineData("400")]
    public void A_value_that_is_not_a_double_collapses_the_clock(object value)
    {
        Assert.Equal(Visibility.Collapsed, Convert(value));
    }

    [Fact]
    public void Converting_back_is_not_supported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(Visibility.Visible, typeof(double), null, CultureInfo.InvariantCulture));
    }
}
