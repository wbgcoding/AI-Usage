using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class WindowEdgeResizeTests
{
    [Fact]
    public void Dragging_the_left_edge_outwards_widens_the_window_and_moves_its_left_edge_with_it()
    {
        var (width, left) = WindowEdgeResize.FromNearEdge(560, 300, -40, 480, 1920);

        Assert.Equal(600, width);
        Assert.Equal(260, left);
    }

    [Fact]
    public void At_the_maximum_width_a_further_outward_drag_changes_neither_width_nor_left()
    {
        var left = WindowEdgeResize.FromNearEdge(1920, 0, -50, 480, 1920);
        var right = WindowEdgeResize.FromFarEdge(1920, 0, 50, 480, 1920);

        Assert.Equal((1920d, 0d), left);
        Assert.Equal((1920d, 0d), right);
    }

    [Fact]
    public void A_left_drag_that_would_pass_the_maximum_moves_the_edge_only_by_the_real_change()
    {
        var (width, left) = WindowEdgeResize.FromNearEdge(1900, 100, -100, 480, 1920);

        Assert.Equal(1920, width);
        Assert.Equal(80, left);
    }

    [Fact]
    public void At_the_minimum_width_a_further_inward_drag_changes_nothing()
    {
        Assert.Equal((480d, 300d), WindowEdgeResize.FromNearEdge(480, 300, 60, 480, 1920));
        Assert.Equal((480d, 300d), WindowEdgeResize.FromFarEdge(480, 300, -60, 480, 1920));
    }

    [Fact]
    public void An_unbounded_maximum_leaves_the_width_free()
    {
        Assert.Equal((700d, 300d), WindowEdgeResize.FromFarEdge(560, 300, 140, 480, double.PositiveInfinity));
    }

    [Fact]
    public void A_maximum_below_the_minimum_does_not_throw()
    {
        var (width, _) = WindowEdgeResize.FromFarEdge(480, 0, 100, 480, 400);

        Assert.Equal(480, width);
    }

    [Fact]
    public void Dragging_the_top_edge_down_shortens_the_window_and_moves_its_top_with_it()
    {
        var (height, top) = WindowEdgeResize.FromNearEdge(600, 100, 40, 300, 1000);

        Assert.Equal(560, height);
        Assert.Equal(140, top);
    }

    [Fact]
    public void The_far_edge_stays_where_it_was_whatever_the_new_size()
    {
        Assert.Equal(160, WindowEdgeResize.AnchorFarEdge(100, 600, 540));
        Assert.Equal(60, WindowEdgeResize.AnchorFarEdge(100, 600, 640));
    }

    [Fact]
    public void A_window_that_still_sizes_itself_is_not_placed_natively()
    {
        Assert.True(WindowEdgeResize.CanPlaceNatively(10, 20, 300, 400));
        Assert.False(WindowEdgeResize.CanPlaceNatively(10, 20, 300, double.NaN));
        Assert.False(WindowEdgeResize.CanPlaceNatively(double.NaN, 20, 300, 400));
        Assert.False(WindowEdgeResize.CanPlaceNatively(10, 20, double.PositiveInfinity, 400));
    }
}
