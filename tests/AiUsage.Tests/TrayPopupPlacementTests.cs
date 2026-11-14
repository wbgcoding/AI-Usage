using System.Windows;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Where the tray popup goes: next to the icon, inside the work area, with every taskbar edge
/// and a monitor of another scaling.</summary>
public class TrayPopupPlacementTests
{
    private static readonly Size Popup = new(200, 120);

    // 1920x1080 at 100 %, taskbar 40 px high on each edge in turn.
    private static readonly Rect BottomTaskbar = new(0, 0, 1920, 1040);
    private static readonly Rect TopTaskbar = new(0, 40, 1920, 1040);
    private static readonly Rect LeftTaskbar = new(48, 0, 1872, 1080);
    private static readonly Rect RightTaskbar = new(0, 0, 1872, 1080);

    private static void AssertInside(PopupPlacement p, Rect area)
    {
        Assert.True(p.X >= area.Left, $"x {p.X} left of {area.Left}");
        Assert.True(p.Y >= area.Top, $"y {p.Y} above {area.Top}");
        Assert.True(p.X + p.Width <= area.Right, $"right edge {p.X + p.Width} beyond {area.Right}");
        Assert.True(p.Y + p.Height <= area.Bottom, $"bottom edge {p.Y + p.Height} beyond {area.Bottom}");
    }

    [Fact]
    public void A_bottom_taskbar_puts_the_popup_above_the_icon_centred_on_it()
    {
        var p = TrayPopupPlacement.Place(new Point(1800, 1060), BottomTaskbar, Popup, 1);

        Assert.Equal(1700, p.X);
        Assert.Equal(1040 - 120 - TrayPopupPlacement.Gap, p.Y);
        Assert.Equal((200, 120), (p.Width, p.Height));
        AssertInside(p, BottomTaskbar);
    }

    [Fact]
    public void An_icon_at_the_right_screen_edge_pulls_the_popup_back_inside()
    {
        var p = TrayPopupPlacement.Place(new Point(1915, 1060), BottomTaskbar, Popup, 1);

        Assert.Equal(1920 - 200, p.X);
        AssertInside(p, BottomTaskbar);
    }

    [Fact]
    public void An_icon_at_the_left_screen_edge_pulls_the_popup_back_inside()
    {
        var p = TrayPopupPlacement.Place(new Point(5, 1060), BottomTaskbar, Popup, 1);

        Assert.Equal(0, p.X);
        AssertInside(p, BottomTaskbar);
    }

    [Fact]
    public void A_top_taskbar_puts_the_popup_below_the_icon()
    {
        var p = TrayPopupPlacement.Place(new Point(1800, 20), TopTaskbar, Popup, 1);

        Assert.Equal(40 + TrayPopupPlacement.Gap, p.Y);
        Assert.Equal(1700, p.X);
        AssertInside(p, TopTaskbar);
    }

    [Fact]
    public void A_left_taskbar_puts_the_popup_beside_it_centred_on_the_icon_height()
    {
        var p = TrayPopupPlacement.Place(new Point(24, 500), LeftTaskbar, Popup, 1);

        Assert.Equal(48 + TrayPopupPlacement.Gap, p.X);
        Assert.Equal(500 - 60, p.Y);
        AssertInside(p, LeftTaskbar);
    }

    [Fact]
    public void A_right_taskbar_puts_the_popup_beside_it_centred_on_the_icon_height()
    {
        var p = TrayPopupPlacement.Place(new Point(1896, 500), RightTaskbar, Popup, 1);

        Assert.Equal(1872 - 200 - TrayPopupPlacement.Gap, p.X);
        Assert.Equal(500 - 60, p.Y);
        AssertInside(p, RightTaskbar);
    }

    [Fact]
    public void A_side_taskbar_icon_near_the_bottom_keeps_the_popup_inside_vertically()
    {
        var p = TrayPopupPlacement.Place(new Point(24, 1075), LeftTaskbar, Popup, 1);

        Assert.Equal(1080 - 120, p.Y);
        AssertInside(p, LeftTaskbar);
    }

    [Fact]
    public void An_anchor_inside_the_work_area_goes_above_and_below_when_there_is_no_room_above()
    {
        var above = TrayPopupPlacement.Place(new Point(900, 600), BottomTaskbar, Popup, 1);
        var below = TrayPopupPlacement.Place(new Point(900, 50), BottomTaskbar, Popup, 1);

        Assert.Equal(600 - 120 - TrayPopupPlacement.Gap, above.Y);
        Assert.Equal(50 + TrayPopupPlacement.Gap, below.Y);
    }

    [Fact]
    public void A_second_monitor_left_of_the_primary_with_150_percent_scaling_gets_pixel_sizes_and_negative_coordinates()
    {
        // 1920x1080 DIPs at 150 % = 2880x1620 px, left of the primary monitor, taskbar 72 px at the bottom.
        var work = new Rect(-2880, 0, 2880, 1548);

        var p = TrayPopupPlacement.Place(new Point(-400, 1580), work, Popup, 1.5);

        Assert.Equal((300, 180), (p.Width, p.Height));
        Assert.Equal(-550, p.X);
        Assert.Equal(1548 - 180 - Math.Floor(TrayPopupPlacement.Gap * 1.5 + 0.5), p.Y);
        AssertInside(p, work);

        var corner = TrayPopupPlacement.Place(new Point(-5, 1580), work, Popup, 1.5);
        Assert.Equal(-300, corner.X);
        AssertInside(corner, work);
    }

    [Fact]
    public void A_second_monitor_above_the_primary_with_125_percent_scaling_rounds_the_pixel_size_up()
    {
        var work = new Rect(300, -1200, 1920, 1160);

        var p = TrayPopupPlacement.Place(new Point(2000, -30), work, new Size(201, 121), 1.25);

        Assert.Equal((252, 152), (p.Width, p.Height));
        AssertInside(p, work);
    }

    [Fact]
    public void A_popup_larger_than_the_work_area_is_cut_to_it()
    {
        var work = new Rect(0, 0, 150, 100);

        var p = TrayPopupPlacement.Place(new Point(75, 120), work, Popup, 1);

        Assert.Equal((150, 100), (p.Width, p.Height));
        Assert.Equal((0, 0), (p.X, p.Y));
    }

    [Fact]
    public void A_missing_scale_counts_as_one()
    {
        var p = TrayPopupPlacement.Place(new Point(900, 1060), BottomTaskbar, Popup, 0);

        Assert.Equal((200, 120), (p.Width, p.Height));
    }
}
