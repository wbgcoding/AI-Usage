using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AiUsage.Services;

/// <summary>The three window-manager calls the Mica backdrop is made of, behind an interface so the
/// fallback path is testable with a call that fails. Every method returns an HRESULT, 0 meaning
/// success.</summary>
internal interface IDwmBackdropApi
{
    int SetBackdropType(IntPtr hwnd, int type);
    int SetDarkMode(IntPtr hwnd, bool dark);
    int ExtendFrame(IntPtr hwnd, bool intoWholeWindow);
}

internal sealed class SystemDwmBackdropApi : IDwmBackdropApi
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;

    public int SetBackdropType(IntPtr hwnd, int type) =>
        NativeWindowStyle.SetDwmAttribute(hwnd, DwmwaSystemBackdropType, type);

    public int SetDarkMode(IntPtr hwnd, bool dark) =>
        NativeWindowStyle.SetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, dark ? 1 : 0);

    public int ExtendFrame(IntPtr hwnd, bool intoWholeWindow) =>
        NativeWindowStyle.ExtendFrameIntoClientArea(hwnd, intoWholeWindow);
}

/// <summary>When the Mica window background is used at all. Pure.</summary>
internal static class MicaPolicy
{
    /// <summary>The first Windows 11 build with the documented system backdrop attribute (22H2).</summary>
    public const int MinimumBuild = 22621;

    /// <summary>Only in the "follow Windows" theme, on a Windows 11 build that supports it, on a fully
    /// opaque window (a translucent window would show the desktop through the backdrop), outside
    /// high-contrast mode and only while the setting is on.</summary>
    public static bool ShouldApply(int osBuild, AppTheme theme, int opacityPercent, bool highContrast, bool enabled) =>
        enabled
        && theme == AppTheme.System
        && osBuild >= MinimumBuild
        && opacityPercent >= 100
        && !highContrast;
}

/// <summary>
/// Puts the Mica backdrop behind one window, or takes it away again. It applies only when every window
/// manager call succeeds; any failing call (or an exception) undoes whatever was set, tells the owner to
/// restore the normal opaque background and logs once - never a transparent or black window.
/// </summary>
internal sealed class MicaController(IntPtr hwnd, IDwmBackdropApi dwm, Action<bool> setTransparentBackground, Action<string> log)
{
    private const int BackdropNone = 1;
    private const int BackdropMainWindow = 2;

    private bool _logged;

    public bool IsActive { get; private set; }

    /// <summary>Brings the window in line with <paramref name="wanted"/>; returns whether Mica is
    /// showing afterwards. Called on every theme, opacity and composition change, which is also the
    /// only time a failed attempt is tried again.</summary>
    public bool Update(bool wanted, bool dark)
    {
        if (!wanted)
        {
            if (IsActive)
                Undo();
            IsActive = false;
            setTransparentBackground(false);
            return false;
        }

        if (!TryApply(dark))
            return false;

        IsActive = true;
        setTransparentBackground(true);
        return true;
    }

    /// <summary>Sets dark mode, the backdrop and the frame extension; on any failure undoes them,
    /// restores the opaque background and logs once. Returns whether all of it was set.</summary>
    private bool TryApply(bool dark)
    {
        var ok = false;
        string? failure = null;
        try
        {
            ok = dwm.SetDarkMode(hwnd, dark) == 0
                && dwm.SetBackdropType(hwnd, BackdropMainWindow) == 0
                && dwm.ExtendFrame(hwnd, intoWholeWindow: true) == 0;
            if (!ok)
                failure = "a window manager call failed";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (ok)
            return true;

        Undo();
        IsActive = false;
        setTransparentBackground(false);
        if (!_logged)
        {
            _logged = true;
            log($"Mica background not applied: {failure}.");
        }

        return false;
    }

    private void Undo()
    {
        try
        {
            _ = dwm.SetBackdropType(hwnd, BackdropNone);
            _ = dwm.ExtendFrame(hwnd, intoWholeWindow: false);
        }
        catch (Exception)
        {
            // Nothing left to undo on a machine whose window manager call throws.
        }
    }
}

/// <summary>
/// Owns the Mica state of the app: the setting, the windows it applies to, and the window-local
/// resource overrides that make each window's background transparent and its cards slightly
/// see-through. The shared theme tokens are never touched, so a window without Mica keeps its opaque
/// look.
/// </summary>
internal static class MicaBackdrop
{
    private const string WindowKey = "Bg.Window";
    private const string SurfaceKey = "Bg.Surface";
    private const byte CardAlpha = 0xE0; // 88 %

    private static readonly List<(MicaController Controller, bool FollowsOpacity)> Controllers = [];
    private static bool _enabled = true;
    private static bool _subscribed;

    /// <summary>Whether Mica is on in the settings (default on).</summary>
    public static bool Enabled => _enabled;

    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Reevaluate();
    }

    /// <summary>A card color at 88 % opacity, so the backdrop shows faintly through the tiles.</summary>
    public static Color CardColor(Color color) => Color.FromArgb(CardAlpha, color.R, color.G, color.B);

    /// <summary>Starts managing one real window. Called once per window: the widget when it begins to
    /// follow the opacity setting, every other window when its chrome is set up. Only a window that
    /// follows the opacity setting loses Mica to a translucent opacity; the others are always opaque.</summary>
    public static void Attach(HwndSource source, bool followsOpacity = true, bool alreadyRendered = false)
    {
        if (source.RootVisual is not Window window)
        {
            // The root is set a moment after the source exists: try again once the first frame is up.
            void Retry(object? sender, EventArgs e)
            {
                source.ContentRendered -= Retry;
                if (source.RootVisual is Window)
                    Attach(source, followsOpacity, alreadyRendered: true);
            }

            source.ContentRendered += Retry;
            return;
        }

        var originalBackground = source.CompositionTarget?.BackgroundColor ?? Colors.Black;
        var controller = new MicaController(source.Handle, new SystemDwmBackdropApi(),
            transparent => SetTransparentBackground(window, source, transparent, originalBackground),
            message => LogService.Shared.LogInfo(message));
        var entry = (controller, followsOpacity);
        Controllers.Add(entry);
        source.Disposed += (_, _) => Controllers.Remove(entry);

        if (!_subscribed)
        {
            _subscribed = true;
            ThemeService.Applied += (_, _) => Reevaluate();
        }

        Update(controller, followsOpacity);

        // Attributes and background set before the window was first shown come out wrong (a tinted
        // or missing backdrop) until Mica is switched off and on again while the window is on screen;
        // the same happens after the window moves to another monitor or the displays change. Do
        // exactly that switch, with a rendered frame of the opaque background in between: the second
        // step runs at background priority, after the render pass the first step queued. A cycle
        // asked for while one is pending is folded into it.
        var cyclePending = false;
        void CycleOnScreen()
        {
            if (cyclePending)
                return;
            cyclePending = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!controller.IsActive)
                {
                    cyclePending = false;
                    return;
                }

                controller.Update(false, false);
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    cyclePending = false;
                    Update(controller, followsOpacity);
                });
            });
        }

        var monitor = NativeMonitors.DeviceNameOfWindow(source.Handle);
        source.AddHook((IntPtr hwnd, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg is WmWindowPosChanged or WmDpiChanged or WmDisplayChange)
            {
                var now = NativeMonitors.DeviceNameOfWindow(hwnd);
                if (NeedsCycle(msg, monitor, now))
                    CycleOnScreen();
                monitor = now;
            }

            return IntPtr.Zero;
        });

        if (alreadyRendered)
        {
            CycleOnScreen();
        }
        else
        {
            void FirstRender(object? sender, EventArgs e)
            {
                window.ContentRendered -= FirstRender;
                CycleOnScreen();
            }

            window.ContentRendered += FirstRender;
        }
    }

    private const int WmWindowPosChanged = 0x0047;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    /// <summary>Whether a window message means the backdrop has to be switched off and on again: a DPI
    /// or display change always, a move only when it took the window to another monitor. Pure.</summary>
    internal static bool NeedsCycle(int msg, string? monitorBefore, string? monitorNow) =>
        msg is WmDpiChanged or WmDisplayChange
        || (msg == WmWindowPosChanged && monitorNow is not null && !string.Equals(monitorBefore, monitorNow, StringComparison.Ordinal));

    /// <summary>Re-checks every managed window against the current theme, opacity, contrast mode and
    /// setting; also the entry point for a desktop composition change.</summary>
    public static void Reevaluate()
    {
        foreach (var (controller, followsOpacity) in Controllers.ToArray())
            Update(controller, followsOpacity);
    }

    private static void Update(MicaController controller, bool followsOpacity)
    {
        var (wanted, dark) = Resolve(followsOpacity);
        controller.Update(wanted, dark);
    }

    private static (bool Wanted, bool Dark) Resolve(bool followsOpacity)
    {
        var wanted = MicaPolicy.ShouldApply(Environment.OSVersion.Version.Build, ThemeService.CurrentTheme,
            followsOpacity ? WindowOpacity.CurrentPercent : 100, SystemParameters.HighContrast, _enabled);
        return (wanted, wanted && !ThemeService.IsWindowsUsingLightTheme());
    }

    private static void SetTransparentBackground(Window window, HwndSource source, bool transparent, Color originalBackground)
    {
        MatchChromeToBackdrop(window, transparent);
        if (transparent)
        {
            window.Resources[WindowKey] = Frozen(Colors.Transparent);
            if (Application.Current?.Resources[SurfaceKey] is SolidColorBrush surface)
                window.Resources[SurfaceKey] = Frozen(CardColor(surface.Color));
            if (source.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
        }
        else
        {
            window.Resources.Remove(WindowKey);
            window.Resources.Remove(SurfaceKey);
            if (source.CompositionTarget is { } target)
                target.BackgroundColor = originalBackground;
        }
    }

    /// <summary>A shell WindowChrome without a glass frame cuts the window to a region on every move
    /// and size change, and a window with a region gets no backdrop. With the glass frame over the
    /// whole window it drops the region and extends the frame itself, as Mica needs. The glass frame
    /// stays once set: taking it away again would put a region back on the visible window, and with
    /// Mica off the opaque window background covers the glass anyway. A window without a WindowChrome
    /// is left alone.</summary>
    internal static void MatchChromeToBackdrop(Window window, bool backdrop)
    {
        if (backdrop && WindowChrome.GetWindowChrome(window) is { IsFrozen: false } chrome)
            chrome.GlassFrameThickness = new Thickness(-1);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
