using System.Windows.Interop;

namespace AiUsage.Services;

/// <summary>
/// One-line bootstrap every custom-chrome window calls right after <c>InitializeComponent</c>:
/// applies the current window opacity (<see cref="WindowOpacity"/>) to the widget itself and, on
/// Windows 11, the DWM
/// rounded-corner preference that replaced the rounded corners this app used to get for free from
/// painting a rounded card on top of an <c>AllowsTransparency=True</c> window background.
/// </summary>
public static class WindowChromeNative
{
    /// <param name="roundedCorners">False for a window that never used the rounded chrome card in the
    /// first place (<see cref="Views.SignInWindow"/>, which hosts a WebView2 control and has always
    /// been a plain square window) - corner rounding does not apply.</param>
    /// <param name="followsOpacity">True only for the widget: the opacity setting is there to let the
    /// desktop show through it, and a settings, statistics or dialog window that turned translucent
    /// with it was just harder to read.</param>
    public static void Bootstrap(Window window, bool roundedCorners = true, bool followsOpacity = false)
    {
        // A window that starts translucent is layered before its first frame; one repaint after that
        // frame makes sure the layered surface actually carries it.
        EventHandler? firstFrame = null;
        firstFrame = (_, _) =>
        {
            window.ContentRendered -= firstFrame;
            if (followsOpacity && PresentationSource.FromVisual(window) is HwndSource renderedSource)
                WindowOpacity.Redraw(renderedSource.Handle);
        };
        window.ContentRendered += firstFrame;

        if (PresentationSource.FromVisual(window) is HwndSource hwndSource)
        {
            Apply(hwndSource.Handle, roundedCorners, followsOpacity);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource lateSource)
                Apply(lateSource.Handle, roundedCorners, followsOpacity);
        };
    }

    private static void Apply(IntPtr hwnd, bool roundedCorners, bool followsOpacity)
    {
        if (followsOpacity)
            WindowOpacity.Follow(hwnd);
        if (!roundedCorners)
            return;

        NativeWindowStyle.TrySetRoundedCorners(hwnd);
        if (!NativeWindowStyle.SystemRoundingProbe(hwnd))
            KeepRegionRounded(hwnd);
    }

    private const int WmSize = 0x0005;

    /// <summary>The chrome border's own corner radius (<c>Radius.Window</c> in the tokens), which is
    /// the radius Windows rounds a window with, so the border and the system clip coincide; also the
    /// size the fallback region is cut to.</summary>
    private const double ChromeRadiusDips = 8;

    /// <summary>Fallback for a machine where Windows does not round the window: cuts it to the
    /// chrome's rounded shape now and again after every size change (the user resizing, a collapse to
    /// the title bar, a DPI change).</summary>
    private static void KeepRegionRounded(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd) is not { } source)
            return;

        void Reapply()
        {
            var scale = source.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            NativeWindowStyle.ApplyRoundedRegion(hwnd, NativeWindowStyle.RegionRadiusPixels(ChromeRadiusDips, scale));
        }

        source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg == WmSize)
                Reapply();
            return IntPtr.Zero;
        });
        Reapply();
    }
}
