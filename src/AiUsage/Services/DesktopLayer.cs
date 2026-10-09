using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AiUsage.Services;

/// <summary>Keeps a window on the desktop level: behind every other window, still visible when the
/// desktop is shown. While active, every z-order change Windows makes to the window (activating it,
/// clicking it) is rewritten to "bottom of the stack" before it happens, and when Show Desktop
/// (Win+D) minimizes the window the foreground switch to the desktop brings it back without
/// activating it.</summary>
internal sealed class DesktopLayer : IDisposable
{
    internal const int WmWindowPosChanging = 0x46;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly IntPtr _hwnd;
    private readonly Func<bool> _leaveAlone;
    private readonly bool _native;
    private readonly WinEventProc _foregroundCallback;
    private IntPtr _hook;
    private DispatcherTimer? _settleTimer;

    /// <param name="hwnd">The window to keep on the desktop level.</param>
    /// <param name="leaveAlone">True while the window is hidden or minimized on purpose, so a switch to
    /// the desktop must not bring it back.</param>
    /// <param name="native">False in tests: no hook, no window calls, only the decisions.</param>
    public DesktopLayer(IntPtr hwnd, Func<bool> leaveAlone, bool native = true)
    {
        _hwnd = hwnd;
        _leaveAlone = leaveAlone;
        _native = native;
        _foregroundCallback = OnForegroundChanged;
    }

    /// <summary>Whether the window is currently held at the bottom.</summary>
    public bool Active { get; private set; }

    /// <summary>Starts or stops holding the window at the bottom. Stopping puts it back at the top of
    /// the ordinary windows, where a normal window would be.</summary>
    public void SetActive(bool active)
    {
        if (active == Active)
            return;

        Active = active;
        if (active)
        {
            if (_native)
            {
                _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _foregroundCallback,
                    0, 0, WineventOutOfContext | WineventSkipOwnProcess);
                SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            return;
        }

        if (!_native)
            return;
        Unhook();
        SetWindowPos(_hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Handles WM_WINDOWPOSCHANGING for the window: while active, whatever position Windows is
    /// about to apply is changed to "bottom of the stack".</summary>
    public void OnWindowPosChanging(IntPtr lParam)
    {
        if (!Active || lParam == IntPtr.Zero)
            return;

        var position = Marshal.PtrToStructure<WindowPos>(lParam);
        if (!ForceBottom(ref position))
            return;
        Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
    }

    /// <summary>Rewrites a pending position change so the window ends up at the bottom. False when it
    /// already did and nothing changed.</summary>
    internal static bool ForceBottom(ref WindowPos position)
    {
        if (position.HwndInsertAfter == HwndBottom && (position.Flags & SwpNoZOrder) == 0)
            return false;

        position.HwndInsertAfter = HwndBottom;
        position.Flags &= ~SwpNoZOrder;
        return true;
    }

    /// <summary>The window classes of the desktop itself, which receive the foreground when the desktop
    /// is shown or clicked.</summary>
    internal static bool IsDesktopWindowClass(string? className) =>
        className is "Progman" or "WorkerW";

    /// <summary>Whether a switch to the desktop must bring the window back: it sits on the desktop level
    /// and Show Desktop minimized or hid it, but the person did not minimize or hide it on purpose.</summary>
    internal static bool ShouldReshow(bool active, bool leaveAlone, bool minimized, bool nativeVisible) =>
        active && !leaveAlone && (minimized || !nativeVisible);

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Active || !IsDesktopWindowClass(ClassNameOf(hwnd)))
            return;

        TryReshow();
        // Show Desktop can still be minimizing windows when the foreground event arrives.
        _settleTimer ??= new DispatcherTimer(SettleDelay, DispatcherPriority.Normal, (_, _) =>
        {
            _settleTimer?.Stop();
            TryReshow();
        }, Dispatcher.CurrentDispatcher);
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void TryReshow()
    {
        if (!ShouldReshow(Active, _leaveAlone(), IsIconic(_hwnd), IsWindowVisible(_hwnd)))
            return;

        ShowWindow(_hwnd, SwShowNoActivate);
        SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private void Unhook()
    {
        _settleTimer?.Stop();
        if (_hook == IntPtr.Zero)
            return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose()
    {
        Active = false;
        if (_native)
            Unhook();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
