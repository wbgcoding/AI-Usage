using System.Windows;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Which point the tray popup hangs on, which side of the monitor the taskbar is on, and the
/// placement against the icon rectangle, including monitors at negative coordinates.</summary>
public class TrayAnchorTests
{
    private static readonly Size Popup = new(200, 120);
    private static readonly Point Cursor = new(900, 700);

    // A bottom taskbar on a 2560x1440 primary monitor (work area 1392 high), the icon at its right end.
    private static readonly Rect PrimaryBounds = new(0, 0, 2560, 1440);
    private static readonly Rect PrimaryWork = new(0, 0, 2560, 1392);
    private static readonly Rect PrimaryIcon = R(2400, 1400, 2424, 1424);

    // Further monitors of a multi-monitor layout, partly above and left of the primary one.
    private static readonly Rect RightBounds = new(2560, -286, 1920, 1080);
    private static readonly Rect RightWorkTopTaskbar = new(2560, -246, 1920, 1040);
    private static readonly Rect LeftBounds = new(-3840, -286, 3840, 2160);
    private static readonly Rect LeftWork = new(-3840, -286, 3840, 2112);
    private static readonly Rect WideBounds = new(3563, -766, 1920, 480);

    private static Rect R(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);

    private static void AssertInside(PopupPlacement p, Rect area)
    {
        Assert.True(p.X >= area.Left && p.Y >= area.Top && p.X + p.Width <= area.Right && p.Y + p.Height <= area.Bottom,
            $"{p} not inside {area}");
    }

    [Fact]
    public void A_callback_point_of_one_zero_with_an_icon_rect_at_the_bottom_places_the_popup_above_the_icon()
    {
        var anchor = TrayAnchorResolver.Resolve(PrimaryIcon, new Point(1, 0), Cursor);

        var p = TrayPopupPlacement.Place(anchor, PrimaryBounds, PrimaryWork, Popup, 1);

        Assert.Equal(TrayAnchorSource.Icon, anchor.Source);
        Assert.Equal(new Point(2412, 1412), anchor.Point);
        Assert.Equal(PrimaryWork.Bottom - 120 - TrayPopupPlacement.Gap, p.Y);
        Assert.Equal(2412 - 100, p.X);
        AssertInside(p, PrimaryWork);
    }

    [Fact]
    public void Without_an_icon_rect_the_callback_point_is_used()
    {
        var anchor = TrayAnchorResolver.Resolve(null, new Point(2412, 1412), Cursor);

        Assert.Equal((new Point(2412, 1412), TrayAnchorSource.WParam, (Rect?)null), (anchor.Point, anchor.Source, anchor.IconRect));
    }

    [Fact]
    public void Without_an_icon_rect_and_without_a_callback_point_the_cursor_is_used()
    {
        Assert.Equal(TrayAnchorSource.Cursor, TrayAnchorResolver.Resolve(null, null, Cursor).Source);
        Assert.Equal(Cursor, TrayAnchorResolver.Resolve(null, new Point(0, 0), Cursor).Point);
        Assert.Equal(TrayAnchorSource.Cursor, TrayAnchorResolver.Resolve(Rect.Empty, null, Cursor).Source);
        Assert.Equal(TrayAnchorSource.Cursor, TrayAnchorResolver.Resolve(new Rect(5, 5, 0, 0), null, Cursor).Source);
    }

    [Fact]
    public void A_callback_point_inside_the_icon_rect_is_kept()
    {
        var anchor = TrayAnchorResolver.Resolve(PrimaryIcon, new Point(2405, 1405), Cursor);

        Assert.Equal((new Point(2405, 1405), TrayAnchorSource.WParam), (anchor.Point, anchor.Source));
    }

    [Fact]
    public void A_callback_point_outside_the_icon_rect_is_dropped_for_the_rect_centre()
    {
        var anchor = TrayAnchorResolver.Resolve(PrimaryIcon, new Point(100, 100), Cursor);

        Assert.Equal((new Point(2412, 1412), TrayAnchorSource.Icon), (anchor.Point, anchor.Source));
    }

    [Theory]
    [InlineData(1200, 1410, TaskbarEdge.Bottom)]
    [InlineData(1200, -10, TaskbarEdge.Top)]
    [InlineData(10, 700, TaskbarEdge.Left)]
    [InlineData(2550, 700, TaskbarEdge.Right)]
    public void The_edge_is_the_side_of_the_work_area_the_icon_lies_outside_of(double x, double y, TaskbarEdge expected)
    {
        var bounds = new Rect(0, 0, 2560, 1440);
        var work = new Rect(48, 40, 2464, 1352);

        Assert.Equal(expected, TrayAnchorResolver.DetectEdge(new Point(x, y), bounds, work));
    }

    [Fact]
    public void An_icon_inside_the_work_area_takes_the_edge_where_the_monitor_is_larger_than_its_work_area()
    {
        // A flyout of hidden icons sits above the bottom taskbar, inside the work area.
        Assert.Equal(TaskbarEdge.Bottom, TrayAnchorResolver.DetectEdge(new Point(2300, 1300), PrimaryBounds, PrimaryWork));
        Assert.Equal(TaskbarEdge.Top, TrayAnchorResolver.DetectEdge(new Point(4000, 100), RightBounds, RightWorkTopTaskbar));
        Assert.Equal(TaskbarEdge.Left, TrayAnchorResolver.DetectEdge(new Point(300, 300), new Rect(0, 0, 1000, 800), new Rect(50, 0, 950, 800)));
        Assert.Equal(TaskbarEdge.Right, TrayAnchorResolver.DetectEdge(new Point(300, 300), new Rect(0, 0, 1000, 800), new Rect(0, 0, 950, 800)));
    }

    [Fact]
    public void With_an_auto_hide_taskbar_the_edge_is_the_monitor_edge_nearest_the_icon()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        Assert.Equal(TaskbarEdge.Bottom, TrayAnchorResolver.DetectEdge(new Point(1800, 1070), bounds, bounds));
        Assert.Equal(TaskbarEdge.Top, TrayAnchorResolver.DetectEdge(new Point(1800, 3), bounds, bounds));
    }

    [Fact]
    public void A_top_taskbar_on_a_monitor_above_the_primary_one_puts_the_popup_below_the_icon()
    {
        var icon = R(4300, -280, 4324, -256);
        var anchor = TrayAnchorResolver.Resolve(icon, new Point(1, 0), Cursor);

        var p = TrayPopupPlacement.Place(anchor, RightBounds, RightWorkTopTaskbar, Popup, 1);

        Assert.Equal(RightWorkTopTaskbar.Top + TrayPopupPlacement.Gap, p.Y);
        AssertInside(p, RightWorkTopTaskbar);
    }

    [Fact]
    public void A_bottom_taskbar_on_a_monitor_left_of_and_above_the_primary_one_puts_the_popup_above_the_icon()
    {
        var icon = R(-200, 1830, -176, 1854);
        var anchor = TrayAnchorResolver.Resolve(icon, null, Cursor);

        var p = TrayPopupPlacement.Place(anchor, LeftBounds, LeftWork, Popup, 1);

        Assert.Equal(LeftWork.Bottom - 120 - TrayPopupPlacement.Gap, p.Y);
        Assert.Equal(-188 - 100, p.X);
        AssertInside(p, LeftWork);
    }

    [Fact]
    public void A_left_taskbar_puts_the_popup_beside_the_icon()
    {
        var bounds = new Rect(0, 0, 1920, 1080);
        var work = new Rect(48, 0, 1872, 1080);
        var anchor = TrayAnchorResolver.Resolve(R(12, 1000, 36, 1024), null, Cursor);

        var p = TrayPopupPlacement.Place(anchor, bounds, work, Popup, 1);

        Assert.Equal(48 + TrayPopupPlacement.Gap, p.X);
        Assert.Equal(1012 - 60, p.Y);
        AssertInside(p, work);
    }

    [Fact]
    public void A_right_taskbar_puts_the_popup_beside_the_icon()
    {
        var bounds = new Rect(3563, -766, 1920, 480);
        var work = new Rect(3563, -766, 1872, 480);
        var anchor = TrayAnchorResolver.Resolve(R(5450, -400, 5474, -376), null, Cursor);

        var p = TrayPopupPlacement.Place(anchor, bounds, work, Popup, 1);

        Assert.Equal(work.Right - 200 - TrayPopupPlacement.Gap, p.X);
        AssertInside(p, work);
    }

    [Fact]
    public void A_short_monitor_keeps_a_tall_popup_inside_its_work_area()
    {
        var work = new Rect(3563, -766, 1920, 432);
        var anchor = TrayAnchorResolver.Resolve(R(5300, -334, 5324, -310), null, Cursor);

        var p = TrayPopupPlacement.Place(anchor, WideBounds, work, new Size(200, 600), 1);

        Assert.Equal(432, p.Height);
        AssertInside(p, work);
    }

    [Fact]
    public void An_icon_in_a_flyout_above_the_bottom_taskbar_puts_the_popup_above_the_flyout_icon()
    {
        var icon = R(2300, 1300, 2324, 1324);
        var anchor = TrayAnchorResolver.Resolve(icon, null, Cursor);

        var p = TrayPopupPlacement.Place(anchor, PrimaryBounds, PrimaryWork, Popup, 1);

        Assert.Equal(1300 - 120 - TrayPopupPlacement.Gap, p.Y);
    }

    [Fact]
    public void Placement_scales_with_the_monitor()
    {
        var anchor = TrayAnchorResolver.Resolve(R(3000, 2100, 3048, 2148), null, Cursor);
        var bounds = new Rect(0, 0, 3840, 2160);
        var work = new Rect(0, 0, 3840, 2088);

        var p = TrayPopupPlacement.Place(anchor, bounds, work, Popup, 2);

        Assert.Equal((400, 240), (p.Width, p.Height));
        Assert.Equal(2088 - 240 - 16, p.Y);
    }

    [Fact]
    public void The_log_line_names_anchor_source_rects_size_and_position()
    {
        var anchor = TrayAnchorResolver.Resolve(PrimaryIcon, new Point(1, 0), Cursor);

        var line = TrayAnchorResolver.FormatLog(anchor, PrimaryWork, new Size(200, 120), new Point(2312, 1264));

        Assert.Equal("Tray popup: anchor 2412,1412 (source icon), icon rect 2400,1400,2424,1424, "
            + "work area 0,0,2560,1392, size 200x120, placed 2312,1264", line);
    }

    [Fact]
    public void The_log_line_without_an_icon_rect_says_none_and_the_source()
    {
        var anchor = TrayAnchorResolver.Resolve(null, null, new Point(5, 6));

        Assert.Contains("anchor 5,6 (source cursor), icon rect none,", TrayAnchorResolver.FormatLog(anchor, PrimaryWork, new Size(1, 1), new Point(0, 0)));
    }
}
