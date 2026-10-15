using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class NaturalFirstRowPanelTests
{
    [Theory]
    [InlineData(250, 150, 500, 110, 250)]
    [InlineData(250, 150, 400, 110, 250)]
    [InlineData(250, 150, 330, 110, 220)]
    [InlineData(250, 60, 300, 110, 240)]
    [InlineData(250, 150, 100, 110, 0)]
    [InlineData(250, 150, double.PositiveInfinity, 110, 250)]
    public void First_keeps_its_natural_width_until_the_second_would_lose_its_reserve(
        double first, double second, double available, double reserve, double expected) =>
        Assert.Equal(expected, NaturalFirstRowPanel.FirstWidth(first, second, available, reserve));
}
