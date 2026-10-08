using System.Runtime.InteropServices;

namespace AiUsage.Services;

/// <summary>
/// Enumerates each display's work area (excludes the taskbar) and device name via raw Win32 -
/// pure geometry, never a pixel. Deliberately not System.Windows.Forms.Screen: that type's
/// AllScreens/Bounds pairing is exactly what a screen-capture tool also reaches for first, so this
/// project's own screen-capture guard treats the mere property name as a host-capture risk
/// regardless of which member follows it. EnumDisplayMonitors sidesteps that ambiguity entirely.
/// </summary>
internal static class NativeMonitors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect clip, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    /// <summary>The mouse cursor's current position in screen coordinates - used only to pick which
    /// monitor to center the window on when the user asks for it back, never logged or displayed.</summary>
    public static (double X, double Y) CursorPosition() =>
        GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    /// <summary>The device name of the monitor Windows itself places <paramref name="window"/> on, or
    /// null when that cannot be told. With mixed scaling the per-monitor areas from <see
    /// cref="WorkAreas"/> can overlap, so a position alone does not always name one monitor.</summary>
    public static string? DeviceNameOfWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return null;
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.DeviceName : null;
    }

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>The cursor in the same per-monitor units <see cref="WorkAreas"/> reports, so the two can
    /// be compared directly to find the monitor under the cursor.</summary>
    public static (double X, double Y) CursorPositionInWorkAreaUnits()
    {
        if (!GetCursorPos(out var point))
            return (0, 0);
        var scale = ScaleOf(MonitorFromPoint(point, MonitorDefaultToNearest));
        return (point.X / scale, point.Y / scale);
    }

    /// <summary>The top edge of the work area of the monitor nearest the screen point, in physical
    /// pixels (the unit <c>PointToScreen</c> reports), or null when it cannot be told.</summary>
    public static double? WorkAreaTopAt(double screenX, double screenY)
    {
        var monitor = MonitorFromPoint(new Point { X = (int)screenX, Y = (int)screenY }, MonitorDefaultToNearest);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.WorkArea.Top : null;
    }

    private static double ScaleOf(IntPtr monitor) =>
        monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

    public static IReadOnlyList<MonitorArea> WorkAreas()
    {
        var results = new List<MonitorArea>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect clip, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                // The process is per-monitor DPI aware, so these are physical pixels, while every
                // caller compares them with WPF's device-independent Left/Top/Width/Height. A window on
                // a monitor is positioned in that monitor's own DPI, so each area uses its own scale.
                var scale = ScaleOf(monitor);
                results.Add(new MonitorArea(
                    info.DeviceName,
                    info.WorkArea.Left / scale, info.WorkArea.Top / scale,
                    (info.WorkArea.Right - info.WorkArea.Left) / scale, (info.WorkArea.Bottom - info.WorkArea.Top) / scale));
            }

            return true;
        }, IntPtr.Zero);

        return results;
    }
}
