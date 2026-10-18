using System.Windows.Media;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

public sealed class ChartDrawCacheTests
{
    [Fact]
    public void ABrushOfOneColorIsSharedAndFrozen()
    {
        var first = ChartDrawCache.BrushFor(Color.FromRgb(10, 20, 30));
        var second = ChartDrawCache.BrushFor(Color.FromRgb(10, 20, 30));

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
    }

    [Fact]
    public void APenIsReusedPerBrushAndShapeAndFrozenWithAFrozenBrush()
    {
        var brush = ChartDrawCache.BrushFor(Color.FromRgb(1, 2, 3));

        var solid = ChartDrawCache.PenFor(brush, 1);
        var dotted = ChartDrawCache.PenFor(brush, 1, dotted: true);

        Assert.Same(solid, ChartDrawCache.PenFor(brush, 1));
        Assert.NotSame(solid, dotted);
        Assert.Equal(DashStyles.Dot, dotted.DashStyle);
        Assert.NotSame(solid, ChartDrawCache.PenFor(brush, 2));
        Assert.True(solid.IsFrozen);
        Assert.Equal(PenLineCap.Round, ChartDrawCache.PenFor(brush, 1.5, round: true).StartLineCap);
    }
}
