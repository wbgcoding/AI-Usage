using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Decoding of the shell's tray callback, with version 4 and without it.</summary>
public class TrayCallbackTests
{
    private static IntPtr Wp(int x, int y) => (IntPtr)(((long)(ushort)(short)y << 16) | (ushort)(short)x);

    // Version 4: the icon id (1) sits in the high word of lParam.
    private static IntPtr V4(int code) => (IntPtr)((1L << 16) | (uint)code);

    [Fact]
    public void The_popup_open_and_close_events_carry_the_icon_anchor_in_wParam()
    {
        var open = TrayCallback.Decode(Wp(1800, 1050), V4(TrayCallback.NinPopupOpen), versionFour: true);
        var close = TrayCallback.Decode(Wp(1800, 1050), V4(TrayCallback.NinPopupClose), versionFour: true);

        Assert.Equal(new TrayEvent(TrayEventKind.PopupOpen, 1800, 1050, true), open);
        Assert.Equal(TrayEventKind.PopupClose, close.Kind);
    }

    [Fact]
    public void The_anchor_of_a_monitor_left_of_and_above_the_primary_is_negative()
    {
        var e = TrayCallback.Decode(Wp(-1500, -30), V4(TrayCallback.NinPopupOpen), versionFour: true);

        Assert.Equal((-1500, -30, true), (e.X, e.Y, e.HasAnchor));
    }

    [Fact]
    public void An_anchor_of_zero_is_no_anchor()
    {
        Assert.False(TrayCallback.Decode(IntPtr.Zero, V4(TrayCallback.NinPopupOpen), versionFour: true).HasAnchor);
    }

    [Fact]
    public void The_event_is_the_low_word_only_so_the_icon_id_in_the_high_word_does_not_matter()
    {
        var e = TrayCallback.Decode(Wp(10, 10), (IntPtr)((7L << 16) | TrayCallback.WmContextMenu), versionFour: true);

        Assert.Equal(TrayEventKind.ContextMenu, e.Kind);
    }

    [Fact]
    public void Version_four_opens_the_menu_on_the_context_menu_message_only()
    {
        Assert.Equal(TrayEventKind.ContextMenu, TrayCallback.Decode(Wp(5, 5), V4(TrayCallback.WmContextMenu), true).Kind);
        Assert.NotEqual(TrayEventKind.ContextMenu, TrayCallback.Decode(Wp(5, 5), V4(TrayCallback.WmRButtonUp), true).Kind);
    }

    [Fact]
    public void Without_version_four_the_right_button_release_opens_the_menu_and_there_is_no_popup()
    {
        Assert.Equal(TrayEventKind.ContextMenu, TrayCallback.Decode((IntPtr)1, (IntPtr)TrayCallback.WmRButtonUp, false).Kind);
        Assert.Equal(TrayEventKind.None, TrayCallback.Decode((IntPtr)1, (IntPtr)TrayCallback.NinPopupOpen, false).Kind);
        Assert.False(TrayCallback.Decode((IntPtr)1, (IntPtr)TrayCallback.WmRButtonUp, false).HasAnchor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_double_click_decodes_in_both_modes(bool versionFour)
    {
        var lParam = versionFour ? V4(TrayCallback.WmLButtonDblClk) : (IntPtr)TrayCallback.WmLButtonDblClk;

        Assert.Equal(TrayEventKind.DoubleClick, TrayCallback.Decode(Wp(1, 1), lParam, versionFour).Kind);
    }

    [Theory]
    [InlineData(TrayCallback.WmLButtonDown)]
    [InlineData(TrayCallback.WmLButtonUp)]
    [InlineData(TrayCallback.WmRButtonDown)]
    [InlineData(TrayCallback.WmMButtonDown)]
    [InlineData(TrayCallback.WmMButtonUp)]
    [InlineData(TrayCallback.NinSelect)]
    [InlineData(TrayCallback.NinKeySelect)]
    [InlineData(TrayCallback.NinBalloonUserClick)]
    public void Any_click_or_selection_is_a_click_that_hides_the_popup(int code)
    {
        Assert.Equal(TrayEventKind.Click, TrayCallback.Decode(Wp(1, 1), V4(code), true).Kind);
    }

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0402)]
    [InlineData(0x0404)]
    public void Mouse_moves_and_balloon_timeouts_are_nothing_to_act_on(int code)
    {
        Assert.Equal(TrayEventKind.None, TrayCallback.Decode(Wp(1, 1), V4(code), true).Kind);
    }
}
