using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AiUsage.Services;

/// <summary>Keeps a window on the desktop level: behind every other window, but directly above the
/// desktop itself (wallpaper and icons), so it stays visible when the desktop is shown. The very
/// bottom of the stack would be under the desktop window and never be seen. While active, every
/// z-order change Windows makes to the window (activating it, clicking it) is rewritten to that
/// slot before it happens, and when Show Desktop (Win+D) minimizes the window the foreground switch
/// to the desktop brings it back without activating it.</summary>
internal sealed class DesktopLayer : IDisposable
{
    internal const int WmWindowPosChanging = 0x46;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const long WsExTopmost = 0x8;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly IntPtr _hwnd;
    private readonly Func<bool> _leaveAlone;
    private readonly bool _native;
    private readonly Win32.WinEventProc _foregroundCallback;
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
                _hook = Win32.SetWinEventHook(Win32.EventSystemForeground, Win32.EventSystemForeground, IntPtr.Zero, _foregroundCallback,
                    0, 0, Win32.WineventOutOfContext | WineventSkipOwnProcess);
                PlaceOnDesktop();
            }
            return;
        }

        if (!_native)
            return;
        Unhook();
        SetWindowPos(_hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Handles WM_WINDOWPOSCHANGING for the window: while active, whatever position Windows is
    /// about to apply is changed so the window ends up directly above the desktop.</summary>
    public void OnWindowPosChanging(IntPtr lParam)
    {
        if (!Active || lParam == IntPtr.Zero)
            return;

        var position = Marshal.PtrToStructure<WindowPos>(lParam);
        if (!NeedsSlotCheck(position.Flags))
            return;
        if (!ApplySlot(ref position, CurrentSlot()))
            return;
        Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
    }

    /// <summary>Whether a pending position change can move the window in the stack: only one that
    /// keeps the order (SWP_NOZORDER) and the size (SWP_NOSIZE), a plain drag, never can, so the
    /// stack is not listed for it.</summary>
    internal static bool NeedsSlotCheck(uint flags) => (flags & SwpNoZOrder) == 0 || (flags & SwpNoSize) == 0;

    /// <summary>Where the window belongs in the stack: <c>InPlace</c> when it already sits
    /// directly above the desktop, otherwise the window to insert it after.</summary>
    internal readonly record struct Slot(bool InPlace, IntPtr InsertAfter);

    /// <summary>One top-level window of the stack, as the desktop-level decision needs it.</summary>
    internal readonly record struct StackEntry(IntPtr Handle, bool IsDesktop, bool IsTopmost);

    /// <summary>The slot directly above the topmost visible desktop window, from the whole stack listed
    /// top to bottom. Without a desktop window in the list it is the very bottom. When only topmost
    /// windows (the taskbar) sit above the desktop, the top of the ordinary windows is that slot, and
    /// inserting after a topmost window would make the window topmost itself.</summary>
    internal static Slot ResolveSlot(IReadOnlyList<StackEntry> topToBottom, IntPtr self)
    {
        StackEntry? previous = null;
        foreach (var entry in topToBottom)
        {
            if (entry.IsDesktop && entry.Handle != self)
            {
                if (previous is not { } above || above.IsTopmost)
                    return new Slot(false, HwndTop);
                return above.Handle == self ? new Slot(true, self) : new Slot(false, above.Handle);
            }
            previous = entry;
        }

        return new Slot(false, HwndBottom);
    }

    /// <summary>Rewrites a pending position change so the window ends up in <paramref name="slot"/>. False
    /// when it already did and nothing changed.</summary>
    internal static bool ApplySlot(ref WindowPos position, Slot slot)
    {
        if (slot.InPlace)
        {
            if ((position.Flags & SwpNoZOrder) != 0)
                return false;
            position.Flags |= SwpNoZOrder;
            return true;
        }

        if (position.HwndInsertAfter == slot.InsertAfter && (position.Flags & SwpNoZOrder) == 0)
            return false;

        position.HwndInsertAfter = slot.InsertAfter;
        position.Flags &= ~SwpNoZOrder;
        return true;
    }

    private Slot CurrentSlot()
    {
        if (!_native)
            return new Slot(false, HwndBottom);

        // Top to bottom; the slot is decided by the first desktop window, so the walk ends there.
        var stack = new List<StackEntry>();
        EnumWindows((hwnd, _) =>
        {
            var visible = IsWindowVisible(hwnd);
            var desktop = visible && IsDesktopWindowClass(Win32.ClassNameOf(hwnd));
            var topmost = (Win32.GetWindowLongPtr(hwnd, Win32.GwlExStyle).ToInt64() & WsExTopmost) != 0;
            stack.Add(new StackEntry(hwnd, desktop, topmost));
            return !(desktop && hwnd != _hwnd);
        }, IntPtr.Zero);
        return ResolveSlot(stack, _hwnd);
    }

    private void PlaceOnDesktop()
    {
        var slot = CurrentSlot();
        if (!slot.InPlace)
            SetWindowPos(_hwnd, slot.InsertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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
        if (!Active || !IsDesktopWindowClass(Win32.ClassNameOf(hwnd)))
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

        NativeWindowStyle.ShowNativeNoActivate(_hwnd);
        PlaceOnDesktop();
    }

    private void Unhook()
    {
        _settleTimer?.Stop();
        if (_hook == IntPtr.Zero)
            return;
        Win32.UnhookWinEvent(_hook);
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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
