using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Coverage for the shared chart hover tooltip's own above/below flip - the one piece of its
/// placement logic that stays a pure function of three doubles, so it is unit testable without a
/// live window. Everything else (creating the Popup, measuring its content) needs a real visual
/// tree and is exercised by the VM screenshot walk instead.</summary>
public class ChartTooltipPopupTests
{
    [Fact]
    public void FitsAboveOnMonitor_compares_in_physical_pixels_on_a_scaled_display()
    {
        // 200% scale: a 300 dip tall tooltip needs 600 px. An anchor 500 px down does not clear a
        // work area starting at 0, though 500 - 300 would wrongly say it does.
        Assert.False(ChartTooltipPopup.FitsAboveOnMonitor(anchorScreenTopPx: 500, tooltipHeightDip: 300, workAreaTopPx: 0, dpiScale: 2.0));
        Assert.True(ChartTooltipPopup.FitsAboveOnMonitor(anchorScreenTopPx: 700, tooltipHeightDip: 300, workAreaTopPx: 0, dpiScale: 2.0));
        Assert.True(ChartTooltipPopup.FitsAboveOnMonitor(anchorScreenTopPx: 200, tooltipHeightDip: 60, workAreaTopPx: 0, dpiScale: 1.0));
    }

    [Fact]
    public void FitsAbove_is_true_when_the_tooltip_clears_the_top_of_the_screen()
    {
        // Anchor 200px down the screen, a 60px tall tooltip, screen starts at 0 - plenty of room above.
        Assert.True(ChartTooltipPopup.FitsAbove(anchorScreenTop: 200, tooltipHeight: 60, screenTop: 0));
    }

    [Fact]
    public void FitsAbove_is_false_for_a_top_row_anchor_close_to_the_screen_edge()
    {
        // A top-row cell near y=0 with a tooltip taller than the remaining space above it - the
        // exact case a drawn-in-place tooltip used to clip at the window's own top edge.
        Assert.False(ChartTooltipPopup.FitsAbove(anchorScreenTop: 10, tooltipHeight: 60, screenTop: 0));
    }

    [Fact]
    public void FitsAbove_is_true_exactly_at_the_boundary()
    {
        Assert.True(ChartTooltipPopup.FitsAbove(anchorScreenTop: 60, tooltipHeight: 60, screenTop: 0));
    }

    [Fact]
    public void FitsAbove_accounts_for_a_nonzero_screen_top_on_a_secondary_monitor()
    {
        // A monitor above the primary one has a negative work-area top; the anchor still fits above
        // as long as it clears that monitor's own top edge, not y=0.
        Assert.True(ChartTooltipPopup.FitsAbove(anchorScreenTop: -900, tooltipHeight: 60, screenTop: -1000));
        Assert.False(ChartTooltipPopup.FitsAbove(anchorScreenTop: -950, tooltipHeight: 60, screenTop: -1000));
    }

    [Fact]
    public void PointerPlacement_centers_on_the_pointer_and_sits_twelve_above_it()
    {
        var location = ChartTooltipPopup.PointerPlacement(new System.Windows.Point(100, 200), new System.Windows.Size(60, 40), fitsAbove: true);
        Assert.Equal(70, location.X);
        Assert.Equal(148, location.Y);
    }

    [Fact]
    public void PointerPlacement_drops_twenty_below_the_pointer_when_it_does_not_fit_above()
    {
        var location = ChartTooltipPopup.PointerPlacement(new System.Windows.Point(100, 5), new System.Windows.Size(60, 40), fitsAbove: false);
        Assert.Equal(70, location.X);
        Assert.Equal(25, location.Y);
    }
}
