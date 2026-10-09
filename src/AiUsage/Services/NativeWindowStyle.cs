using System.Runtime.InteropServices;

namespace AiUsage.Services;

/// <summary>
/// Native window styling this app makes outside WPF's own reach and outside <see
/// cref="WindowOpacity"/>'s own layered alpha: WS_EX_TRANSPARENT makes a window ignore the mouse
/// entirely (the click-through overlay mode), which needs WS_EX_LAYERED alongside it to take
/// effect; DWMWA_WINDOW_CORNER_PREFERENCE rounds a window that no longer gets rounded corners from
/// painting a rounded card on an <c>AllowsTransparency=True</c> background. Pure Win32, never a
/// pixel touched - x64/ARM64 only, so the *Ptr entry points are safe to call directly rather than
/// branching on pointer size.
/// </summary>
internal static class NativeWindowStyle
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcRound = 2;

    // Windows 11's build floor - a pre-11 DwmSetWindowAttribute call with the corner-preference
    // attribute simply fails, which TrySetRoundedCorners already tolerates, but checking first avoids
    // making a call nobody there will ever honour.
    private const int Windows11Build = 22000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Click-through needs a layered window, so the layered bit and the alpha follow through
    /// <see cref="WindowOpacity.Apply"/>: turning click-through off on a fully opaque window makes it
    /// an ordinary window again, a translucent one stays layered at its alpha.</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updated = clickThrough ? current | WsExTransparent : current & ~WsExTransparent;
        if (updated != current)
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updated));
        WindowOpacity.Apply(hwnd, WindowOpacity.CurrentPercent);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    /// <summary>Hides the native window without touching what WPF knows about it, so the shown or
    /// hidden state the person chose, the tray and the saved placement stay as they were.</summary>
    public static void HideNative(IntPtr hwnd) => ShowWindow(hwnd, SwHide);

    /// <summary>Shows the native window again without taking the focus from whatever is in front.</summary>
    public static void ShowNativeNoActivate(IntPtr hwnd) => ShowWindow(hwnd, SwShowNoActivate);

    /// <summary>Asks the window manager to round a window's corners - the replacement for the rounded
    /// corners this app used to get for free from painting a rounded card on a fully transparent
    /// window background. No-op (square window) on Windows 10, which has no such attribute; the build
    /// check keeps this from making a call nobody there would honour anyway, and
    /// DwmSetWindowAttribute never throws for a failing HRESULT on top of that, it only returns
    /// one, which this swallows on purpose.</summary>
    public static void TrySetRoundedCorners(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < Windows11Build)
            return;

        var preference = DwmwcRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    /// <summary>Whether Windows rounds this window's corners itself. It does from Windows 11 on, with
    /// desktop composition running, hardware rendering available (without a GPU the window manager
    /// switches rounding off: render tier above zero and a real display adapter) and the corner preference actually set on the window. Pure so both
    /// outcomes are testable.</summary>
    public static bool SystemRoundsCorners(int osBuild, bool compositionEnabled, int renderTier, bool preferenceIsRound, bool hardwareAdapter = true) =>
        osBuild >= Windows11Build && compositionEnabled && renderTier > 0 && preferenceIsRound && hardwareAdapter;

    /// <summary>Whether a display adapter name is one of the software or virtual-machine adapters that
    /// have no hardware acceleration. WPF still reports a render tier above zero on those (it draws
    /// through the software rasterizer), but the window manager does not round without a GPU.</summary>
    public static bool IsSoftwareAdapterName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    private const int DisplayDeviceAttachedToDesktop = 0x1;

    private static bool HasHardwareAdapter()
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
        for (uint index = 0; index < 16 && EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) != 0 && IsSoftwareAdapterName(device.DeviceString))
                return false;
        }

        return true;
    }

    /// <summary>The seam <see cref="WindowChromeNative"/> asks: does Windows round this window? Tests
    /// replace it to drive both outcomes; the default reads the real system state.</summary>
    internal static Func<IntPtr, bool> SystemRoundingProbe { get; set; } = ProbeSystemRounding;

    private static bool ProbeSystemRounding(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < Windows11Build)
            return false;

        var compositionEnabled = DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        var preference = 0;
        var preferenceIsRound = DwmGetWindowAttribute(hwnd, DwmwaWindowCornerPreference, out preference, sizeof(int)) == 0
            && preference == DwmwcRound;
        var renderTier = System.Windows.Media.RenderCapability.Tier >> 16;
        return SystemRoundsCorners(Environment.OSVersion.Version.Build, compositionEnabled, renderTier, preferenceIsRound, HasHardwareAdapter());
    }

    /// <summary>The corner radius in device pixels for a chrome radius in DIPs at the window's DPI
    /// scale - the one conversion the window region and the painted chrome share, so the clip and the
    /// rounded border line up to the pixel.</summary>
    public static int RegionRadiusPixels(double radiusDips, double dpiScale) =>
        Math.Max(0, (int)(radiusDips * Math.Max(dpiScale, 0.01) + 0.5));

    /// <summary>Clips the window itself to a rounded rectangle of <paramref name="radiusPixels"/>.
    /// Only the fallback for a machine where Windows does not round the window (Windows 10, or no
    /// hardware rendering): its square corners would otherwise show around the rounded chrome. Where
    /// the system rounds, a region would switch that rounding and the window shadow off. The system
    /// owns the region handle once it is set.</summary>
    public static void ApplyRoundedRegion(IntPtr hwnd, int radiusPixels)
    {
        if (radiusPixels <= 0 || !GetWindowRect(hwnd, out var rect))
            return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return;

        // CreateRoundRectRgn leaves out the right and bottom edge, hence the extra pixel.
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radiusPixels * 2, radiusPixels * 2);
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region);
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
