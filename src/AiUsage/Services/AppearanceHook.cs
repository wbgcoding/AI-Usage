using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AiUsage.Services;

/// <summary>
/// Window hook that tells the theme code when Windows changes how it looks: the app color mode and
/// accent color (<c>WM_SETTINGCHANGE</c> with "ImmersiveColorSet", <c>WM_DWMCOLORIZATIONCOLORCHANGED</c>)
/// and desktop composition (<c>WM_DWMCOMPOSITIONCHANGED</c>). Attached once to the widget window.
/// </summary>
internal static class AppearanceHook
{
    internal const int WmSettingChange = 0x001A;
    internal const int WmDwmColorizationColorChanged = 0x0320;

    /// <summary>True for a message that means the Windows color mode or accent color may have changed.
    /// Pure, so it is testable without a window.</summary>
    internal static bool IsColorNotice(int msg, string? settingName) =>
        msg == WmDwmColorizationColorChanged
        || (msg == WmSettingChange && string.Equals(settingName, "ImmersiveColorSet", StringComparison.Ordinal));

    public static void Attach(HwndSource source) => source.AddHook(OnMessage);

    private static IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var settingName = msg == WmSettingChange && lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
        if (IsColorNotice(msg, settingName) && ThemeService.CurrentTheme == AppTheme.System)
            ThemeService.Apply(AppTheme.System);

        return IntPtr.Zero;
    }
}
