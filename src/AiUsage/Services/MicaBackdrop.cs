using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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

        if (!ok)
        {
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

        IsActive = true;
        setTransparentBackground(true);
        return true;
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
/// Owns the Mica state of the app: the setting, the widget window it applies to, and the window-local
/// resource overrides that make the window background transparent and the cards slightly see-through.
/// The shared theme tokens are never touched, so other windows keep their opaque look.
/// </summary>
internal static class MicaBackdrop
{
    private const string WindowKey = "Bg.Window";
    private const string SurfaceKey = "Bg.Surface";
    private const byte CardAlpha = 0xE0; // 88 %

    private static readonly List<MicaController> Controllers = [];
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

    /// <summary>Starts managing one real window (the widget). Called once per window, when it begins to
    /// follow the opacity setting.</summary>
    public static void Attach(HwndSource source)
    {
        if (source.RootVisual is not Window window)
        {
            // The root is set a moment after the source exists: try again once the first frame is up.
            void Retry(object? sender, EventArgs e)
            {
                source.ContentRendered -= Retry;
                if (source.RootVisual is Window)
                    Attach(source);
            }

            source.ContentRendered += Retry;
            return;
        }

        var originalBackground = source.CompositionTarget?.BackgroundColor ?? Colors.Black;
        var controller = new MicaController(source.Handle, new SystemDwmBackdropApi(),
            transparent => SetTransparentBackground(window, source, transparent, originalBackground),
            message => LogService.Shared.LogInfo(message));
        Controllers.Add(controller);
        source.Disposed += (_, _) => Controllers.Remove(controller);

        if (!_subscribed)
        {
            _subscribed = true;
            ThemeService.Applied += (_, _) => Reevaluate();
        }

        Update(controller);
    }

    /// <summary>Re-checks every managed window against the current theme, opacity, contrast mode and
    /// setting; also the entry point for a desktop composition change.</summary>
    public static void Reevaluate()
    {
        foreach (var controller in Controllers.ToArray())
            Update(controller);
    }

    private static void Update(MicaController controller)
    {
        var wanted = MicaPolicy.ShouldApply(Environment.OSVersion.Version.Build, ThemeService.CurrentTheme,
            WindowOpacity.CurrentPercent, SystemParameters.HighContrast, _enabled);
        var dark = wanted && !ThemeService.IsWindowsUsingLightTheme();
        controller.Update(wanted, dark);
    }

    private static void SetTransparentBackground(Window window, HwndSource source, bool transparent, Color originalBackground)
    {
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

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
