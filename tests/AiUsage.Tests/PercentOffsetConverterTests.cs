using System.Globalization;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class PercentOffsetConverterTests
{
    private readonly PercentOffsetConverter _converter = new();

    private double Offset(params object[] values) =>
        (double)_converter.Convert(values, typeof(double), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(50.0, 200.0, 99.0)]
    [InlineData(25.0, 400.0, 99.0)]
    [InlineData(10.0, 100.0, 9.0)]
    public void The_tick_is_centred_on_the_percent_position(double percent, double width, double expected)
    {
        Assert.Equal(expected, Offset(percent, width), 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-30.0)]
    [InlineData(0.5)]
    public void A_value_at_or_below_the_start_keeps_the_tick_inside_the_left_end(double percent)
    {
        Assert.Equal(0, Offset(percent, 200.0));
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(150.0)]
    [InlineData(99.9)]
    public void A_value_at_or_above_the_end_keeps_the_tick_inside_the_right_end(double percent)
    {
        Assert.Equal(198, Offset(percent, 200.0));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.0)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void A_track_no_wider_than_the_tick_gives_zero(double width)
    {
        Assert.Equal(0, Offset(50.0, width));
    }

    [Fact]
    public void A_track_just_wider_than_the_tick_stays_inside_it()
    {
        var offset = Offset(100.0, 3.0);

        Assert.Equal(1, offset, 6);
    }

    [Fact]
    public void Missing_values_count_as_zero()
    {
        Assert.Equal(0, Offset());
        Assert.Equal(0, Offset(50.0));
    }

    [Fact]
    public void Values_that_are_not_doubles_count_as_zero()
    {
        Assert.Equal(0, Offset("50", 200.0));
        Assert.Equal(0, Offset(50, 200));
        Assert.Equal(0, Offset(null!, null!));
    }

    [Fact]
    public void A_non_double_percent_still_uses_the_track_width()
    {
        // Percent falls back to 0, so the tick sits at the clamped start of a valid track.
        Assert.Equal(0, Offset(50, 200.0));
    }

    [Fact]
    public void Converting_back_is_not_supported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(10d, [typeof(double), typeof(double)], null, CultureInfo.InvariantCulture));
    }
}
