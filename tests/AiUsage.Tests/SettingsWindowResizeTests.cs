using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

public class SettingsWindowResizeTests
{
    [Fact]
    public void Dragging_the_left_edge_outwards_widens_the_window_and_moves_its_left_edge_with_it()
    {
        var (width, left) = SettingsWindowResize.FromLeftEdge(560, 300, -40, 480, 1920);

        Assert.Equal(600, width);
        Assert.Equal(260, left);
    }

    [Fact]
    public void At_the_maximum_width_a_further_outward_drag_changes_neither_width_nor_left()
    {
        var left = SettingsWindowResize.FromLeftEdge(1920, 0, -50, 480, 1920);
        var right = SettingsWindowResize.FromRightEdge(1920, 0, 50, 480, 1920);

        Assert.Equal((1920d, 0d), left);
        Assert.Equal((1920d, 0d), right);
    }

    [Fact]
    public void A_left_drag_that_would_pass_the_maximum_moves_the_edge_only_by_the_real_change()
    {
        var (width, left) = SettingsWindowResize.FromLeftEdge(1900, 100, -100, 480, 1920);

        Assert.Equal(1920, width);
        Assert.Equal(80, left);
    }

    [Fact]
    public void At_the_minimum_width_a_further_inward_drag_changes_nothing()
    {
        Assert.Equal((480d, 300d), SettingsWindowResize.FromLeftEdge(480, 300, 60, 480, 1920));
        Assert.Equal((480d, 300d), SettingsWindowResize.FromRightEdge(480, 300, -60, 480, 1920));
    }

    [Fact]
    public void An_unbounded_maximum_leaves_the_width_free()
    {
        Assert.Equal((700d, 300d), SettingsWindowResize.FromRightEdge(560, 300, 140, 480, double.PositiveInfinity));
    }

    [Fact]
    public void A_maximum_below_the_minimum_does_not_throw()
    {
        var (width, _) = SettingsWindowResize.FromRightEdge(480, 0, 100, 480, 400);

        Assert.Equal(480, width);
    }
}
