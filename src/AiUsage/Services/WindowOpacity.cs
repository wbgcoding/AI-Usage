using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AiUsage.Services;

/// <summary>
/// Applies the "Deckkraft des Fensters" setting through a DWM-composed, per-window alpha
/// (WS_EX_LAYERED + SetLayeredWindowAttributes) instead of the software-rendered translucent brush
/// this app used to build from <c>AllowsTransparency=True</c>. A layered top-level window still
/// renders and composites on the GPU through the DWM (true since Windows 8) - only the pre-8
/// software-layering path this app never targets was slow, so this is a strict improvement with no
/// AllowsTransparency, no window re-creation, and no restart needed to see a change.
/// </summary>
public static class WindowOpacity
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x80000;
    private const long WsExTransparent = 0x20;
    private const uint RdwInvalidate = 0x1;
    private const uint RdwErase = 0x4;
    private const uint RdwAllChildren = 0x80;
    private const uint RdwUpdateNow = 0x100;
    private const uint RdwFrame = 0x400;
    private const uint LwaAlpha = 0x2;

    /// <summary>The opacity most recently requested - an ambient singleton for the same reason
    /// <see cref="ThemeService.CurrentTheme"/> is one: a window not yet constructed when the user
    /// drags the slider needs to know what to apply to itself once its own handle exists.</summary>
    public static int CurrentPercent { get; private set; } = 100;

    /// <summary>The windows the setting applies to (the widget only, see
    /// <see cref="WindowChromeNative.Bootstrap"/>); every other window stays fully opaque.</summary>
    private static readonly HashSet<IntPtr> Followers = [];

    /// <summary>Makes one real window follow the setting from now on: hooks its style notices,
    /// applies the current opacity and forgets the handle again once the window is gone.</summary>
    public static void Follow(IntPtr hwnd)
    {
        if (!Followers.Add(hwnd))
            return;
        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(HideLayeringFromRenderer);
            AppearanceHook.Attach(source);
            MicaBackdrop.Attach(source);
            source.Disposed += (_, _) => Followers.Remove(hwnd);
        }
        Apply(hwnd, CurrentPercent);
    }

    /// <summary>0-100 (already clamped by <see cref="SettingsRanges.ClampWindowOpacityPercent"/>) to
    /// the 0-255 alpha <c>SetLayeredWindowAttributes</c> wants - pure, so this one line of arithmetic
    /// is unit-testable without a real HWND.</summary>
    public static byte AlphaFromPercent(int percent) => (byte)(Math.Clamp(percent, 0, 100) * 255 / 100);

    /// <summary>The extended style a window needs for <paramref name="percent"/>: layered only while it
    /// is translucent or click-through (WS_EX_TRANSPARENT only works on a layered window), plain
    /// otherwise, so a fully opaque window is an ordinary one. Never touches any other bit. Pure, so
    /// the flag arithmetic is unit-testable without a real HWND.</summary>
    public static long RequiredExStyle(long currentExStyle, int percent)
    {
        var needsLayer = percent < 100 || (currentExStyle & WsExTransparent) != 0;
        return needsLayer ? currentExStyle | WsExLayered : currentExStyle & ~WsExLayered;
    }

    /// <summary>True for an extended-style change that flips nothing but the layered and
    /// click-through bits - the only change this class ever makes. Pure, so it is unit-testable
    /// without a real HWND.</summary>
    public static bool IsOwnLayeringChange(long oldExStyle, long newExStyle)
    {
        var changed = oldExStyle ^ newExStyle;
        return (changed & WsExLayered) != 0 && (changed & ~(WsExLayered | WsExTransparent)) == 0;
    }

    /// <summary>Window hook that keeps the layered bit this class needs. A WPF window without
    /// AllowsTransparency treats WS_EX_LAYERED as foreign: it strips the bit while the change is
    /// still pending (WM_STYLECHANGING) and later writes its own cached extended style back over it,
    /// so the constant alpha set here was dropped again before the first frame and the opacity
    /// setting did nothing. Public hooks run before WPF's own, so this corrects every pending
    /// extended-style change to what the current opacity needs and keeps WPF from seeing the
    /// layering flip at all; every other style bit passes unchanged.</summary>
    public static IntPtr HideLayeringFromRenderer(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg != WmStyleChanged && msg != WmStyleChanging) || wParam.ToInt64() != GwlExStyle || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var styles = Marshal.PtrToStructure<StyleStruct>(lParam);
        var (corrected, hide) = ResolveStyleNotice(msg == WmStyleChanging, styles.Old, styles.New, CurrentPercent);
        if (corrected != styles.New)
            Marshal.WriteInt32(lParam, sizeof(uint), unchecked((int)corrected));
        handled |= hide;
        return IntPtr.Zero;
    }

    /// <summary>The decision behind <see cref="HideLayeringFromRenderer"/>: a pending change
    /// (<paramref name="changing"/>) is corrected to the extended style <paramref name="percent"/>
    /// needs, and the notice is kept from WPF whenever the result is layered or flips only the bits
    /// this class owns. Pure, so it is unit-testable without a real HWND.</summary>
    public static (uint NewStyle, bool Hide) ResolveStyleNotice(bool changing, uint oldStyle, uint newStyle, int percent)
    {
        var corrected = changing ? (uint)RequiredExStyle(newStyle, percent) : newStyle;
        var hide = IsOwnLayeringChange(oldStyle, corrected) || (corrected & WsExLayered) != 0;
        return (corrected, hide);
    }

    private const int WmStyleChanging = 0x7C;
    private const int WmStyleChanged = 0x7D;

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public uint Old;
        public uint New;
    }

    /// <summary>Applies <paramref name="percent"/> to one real window handle. A window that just
    /// became layered, or stopped being layered, shows nothing until it paints again, so a style
    /// change is followed by a full redraw.</summary>
    public static void Apply(IntPtr hwnd, int percent)
    {
        var current = NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updated = RequiredExStyle(current, percent);
        if (updated != current)
            NativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updated));

        if ((updated & WsExLayered) != 0)
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, AlphaFromPercent(percent), LwaAlpha);

        if (updated != current)
            Redraw(hwnd);
    }


    /// <summary>Makes a window paint its whole surface again, frame and children included.</summary>
    public static void Redraw(IntPtr hwnd) =>
        NativeMethods.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
            RdwInvalidate | RdwErase | RdwFrame | RdwAllChildren | RdwUpdateNow);

    /// <summary>Applies <paramref name="percent"/> to every open window that follows the setting
    /// (<see cref="Follow"/>). Also remembers it in <see cref="CurrentPercent"/> for a following
    /// window created afterwards - called on every settings-slider change, never only at startup, so
    /// a change is visible immediately with no window re-creation.</summary>
    public static void ApplyToAllOpenWindows(int percent)
    {
        CurrentPercent = percent;
        foreach (var hwnd in Followers.ToArray())
            Apply(hwnd, percent);
        MicaBackdrop.Reevaluate();
    }

    /// <summary>The P/Invoke seam: kept separate from the pure arithmetic above so
    /// <see cref="AlphaFromPercent"/> and <see cref="RequiredExStyle"/> stay testable with no real window.</summary>
    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    }
}
