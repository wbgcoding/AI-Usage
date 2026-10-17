using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

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
    internal const int WmDwmCompositionChanged = 0x031E;

    /// <summary>True for a message that means the Windows color mode or accent color may have changed.
    /// Pure, so it is testable without a window.</summary>
    internal static bool IsColorNotice(int msg, string? settingName) =>
        msg == WmDwmColorizationColorChanged
        || (msg == WmSettingChange && string.Equals(settingName, "ImmersiveColorSet", StringComparison.Ordinal));

    // Windows sends several of these notices for one change; the theme is applied once they settle.
    private static readonly TimeSpan ColorNoticeSettle = TimeSpan.FromMilliseconds(250);
    private static DispatcherTimer? _pendingApply;

    public static void Attach(HwndSource source) => source.AddHook(OnMessage);

    private static IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDwmCompositionChanged)
        {
            MicaBackdrop.Reevaluate();
            return IntPtr.Zero;
        }

        var settingName = msg == WmSettingChange && lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
        if (IsColorNotice(msg, settingName) && ThemeService.CurrentTheme == AppTheme.System)
            ScheduleApply();

        return IntPtr.Zero;
    }

    private static void ScheduleApply()
    {
        if (_pendingApply is null)
        {
            _pendingApply = new DispatcherTimer(DispatcherPriority.Background) { Interval = ColorNoticeSettle };
            _pendingApply.Tick += (_, _) =>
            {
                _pendingApply!.Stop();
                if (ThemeService.CurrentTheme == AppTheme.System)
                    ThemeService.Apply(AppTheme.System);
            };
        }

        _pendingApply.Stop();
        _pendingApply.Start();
    }
}
