namespace AiUsage.Services;

internal enum TrayEventKind
{
    /// <summary>Nothing this app acts on (mouse moves, balloon timeouts).</summary>
    None,
    /// <summary>The context menu was asked for: right click or the keyboard.</summary>
    ContextMenu,
    DoubleClick,
    /// <summary>The pointer rests on the icon: time to show the popup.</summary>
    PopupOpen,
    /// <summary>The pointer left the icon: time to hide the popup.</summary>
    PopupClose,
    /// <summary>Any other click or selection on the icon (or its balloon): hides the popup.</summary>
    Click,
}

/// <summary>A decoded tray callback. <see cref="X"/> and <see cref="Y"/> are the icon's anchor point in
/// physical screen pixels when <see cref="HasAnchor"/>.</summary>
internal readonly record struct TrayEvent(TrayEventKind Kind, int X, int Y, bool HasAnchor);

/// <summary>
/// Reads the shell's callback message. With <c>NOTIFYICON_VERSION_4</c> the event is the low word of
/// <c>lParam</c> (the icon id sits in the high word) and <c>wParam</c> carries the icon's anchor point,
/// x in the low and y in the high word, each a signed 16 bit value (monitors left of or above the
/// primary one give negative numbers). Without it <c>lParam</c> is the plain mouse message and
/// <c>wParam</c> only the icon id.
/// </summary>
internal static class TrayCallback
{
    internal const int WmContextMenu = 0x007B;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmLButtonDblClk = 0x0203;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int NinSelect = 0x0400;
    internal const int NinKeySelect = 0x0401;
    internal const int NinBalloonUserClick = 0x0405;
    internal const int NinPopupOpen = 0x0406;
    internal const int NinPopupClose = 0x0407;

    internal static TrayEvent Decode(IntPtr wParam, IntPtr lParam, bool versionFour)
    {
        var code = (int)(lParam.ToInt64() & 0xFFFF);
        var (x, y, hasAnchor) = versionFour ? Anchor(wParam) : (0, 0, false);

        var kind = code switch
        {
            WmContextMenu when versionFour => TrayEventKind.ContextMenu,
            // Version 4 sends the context menu request on its own; the raw button message that
            // goes with it must not open a second menu.
            WmRButtonUp => versionFour ? TrayEventKind.Click : TrayEventKind.ContextMenu,
            WmLButtonDblClk => TrayEventKind.DoubleClick,
            NinPopupOpen when versionFour => TrayEventKind.PopupOpen,
            NinPopupClose when versionFour => TrayEventKind.PopupClose,
            WmLButtonDown or WmLButtonUp or WmRButtonDown or WmMButtonDown or WmMButtonUp
                or NinSelect or NinKeySelect or NinBalloonUserClick => TrayEventKind.Click,
            _ => TrayEventKind.None,
        };
        return new TrayEvent(kind, x, y, hasAnchor);
    }

    private static (int X, int Y, bool HasAnchor) Anchor(IntPtr wParam)
    {
        var bits = wParam.ToInt64();
        var x = (short)(bits & 0xFFFF);
        var y = (short)((bits >> 16) & 0xFFFF);
        return (x, y, x != 0 || y != 0);
    }
}
