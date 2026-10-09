using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AiUsage.Services;

/// <summary>A rectangle in screen pixels.</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Whether this rectangle spans all of <paramref name="other"/>.</summary>
    public bool Covers(ScreenRect other) =>
        Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>Tells when a full-screen application is in front, so the widget can step aside while a game,
/// a video or a presentation owns the screen. Event driven: a change of the foreground window, or a
/// resize of the foreground window (a browser going full screen with F11 keeps the foreground), plus a
/// slow poll only while a full-screen app is in front, as a safety net for the way back.</summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int MonitorDefaultToNearest = 2;
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IntPtr _widgetHwnd;
    private readonly WinEventProc _callback;
    private readonly DispatcherTimer _poll;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private bool _enabled;

    public FullscreenWatcher(IntPtr widgetHwnd)
    {
        _widgetHwnd = widgetHwnd;
        _callback = OnWinEvent;
        _poll = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Evaluate(), Dispatcher.CurrentDispatcher);
        _poll.Stop();
    }

    /// <summary>Whether a full-screen application is in front right now (always false while not
    /// <see cref="Enabled"/>).</summary>
    public bool IsFullscreenActive { get; private set; }

    /// <summary>Raised on the UI thread each time <see cref="IsFullscreenActive"/> changes.</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>Starts or stops watching. Stopping while a full-screen app is in front reports the end of
    /// it, so whatever stepped aside comes back.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
                return;

            _enabled = value;
            if (value)
            {
                _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
                _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
                Evaluate();
                return;
            }

            Unhook();
            Set(false);
        }
    }

    /// <summary>Whether the foreground state counts as a full-screen application: the shell reports a
    /// full-screen Direct3D application or presentation mode, or the foreground window is not ours, not
    /// part of the shell and covers the whole monitor the widget is on.</summary>
    internal static bool IsFullscreen(bool foregroundIsOwn, string? className, ScreenRect foreground, ScreenRect monitor, int notificationState)
    {
        if (notificationState is QunsRunningD3dFullScreen or QunsPresentationMode)
            return true;

        if (foregroundIsOwn || IsShellWindowClass(className))
            return false;

        return foreground.Covers(monitor);
    }

    /// <summary>The desktop and the taskbar: they fill the screen without being a full-screen app.</summary>
    internal static bool IsShellWindowClass(string? className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == EventObjectLocationChange && (idObject != ObjidWindow || hwnd != GetForegroundWindow()))
            return;

        Evaluate();
    }

    private void Evaluate()
    {
        if (!_enabled)
        {
            Set(false);
            return;
        }

        Set(Probe());
    }

    private bool Probe()
    {
        var foreground = GetForegroundWindow();
        _ = SHQueryUserNotificationState(out var state);
        if (foreground == IntPtr.Zero)
            return IsFullscreen(false, null, default, default, state);

        _ = GetWindowThreadProcessId(foreground, out var processId);
        var monitor = MonitorBounds();
        var rect = GetWindowRect(foreground, out var window)
            ? new ScreenRect(window.Left, window.Top, window.Right, window.Bottom)
            : default;
        return IsFullscreen(processId == (uint)Environment.ProcessId, ClassNameOf(foreground), rect, monitor, state);
    }

    /// <summary>The whole monitor (taskbar included) the widget is on.</summary>
    private ScreenRect MonitorBounds()
    {
        var monitor = MonitorFromWindow(_widgetHwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom)
            : default;
    }

    private void Set(bool fullscreen)
    {
        if (fullscreen)
            _poll.Start();
        else
            _poll.Stop();

        if (fullscreen == IsFullscreenActive)
            return;

        IsFullscreenActive = fullscreen;
        FullscreenChanged?.Invoke(this, fullscreen);
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private void Unhook()
    {
        foreach (var hook in new[] { _foregroundHook, _locationHook })
        {
            if (hook != IntPtr.Zero)
                UnhookWinEvent(hook);
        }

        _foregroundHook = IntPtr.Zero;
        _locationHook = IntPtr.Zero;
    }

    public void Dispose()
    {
        _enabled = false;
        _poll.Stop();
        Unhook();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
