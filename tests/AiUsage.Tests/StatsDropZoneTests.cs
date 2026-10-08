using AiUsage.Stats;
using AiUsage.Views;

namespace AiUsage.Tests;

public class StatsDropZoneTests
{
    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(0, 0, true)]
    [InlineData(100, 50, true)]
    [InlineData(-1, 10, false)]
    [InlineData(10, 51, false)]
    [InlineData(101, 10, false)]
    public void Contains_is_true_only_inside_the_rectangle(double x, double y, bool expected) =>
        Assert.Equal(expected, StatsDropZone.Contains(100, 50, x, y));

    [Fact]
    public void Compute_is_null_outside_and_picks_a_side_inside()
    {
        Assert.Null(StatsDropZone.Compute(100, 50, 150, 10, targetAlone: true));
        Assert.Equal(StatsDropPosition.Left, StatsDropZone.Compute(100, 50, 5, 10, targetAlone: true));
        Assert.Equal(StatsDropPosition.Below, StatsDropZone.Compute(100, 50, 50, 40, targetAlone: false));
    }
}
