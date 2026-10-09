using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Tray icon and its context menu, built directly on <c>Shell_NotifyIcon</c> (shell32) instead of
/// a UI framework's own wrapper - a hidden message window receives the shell callback, and the
/// right-click menu is a plain WPF <see cref="ContextMenu"/> themed the same way as every other
/// popup in this app. Not unit-testable (a live shell tray icon needs a real desktop
/// session) - only <see cref="TrayTooltipBuilder"/>'s pure text-building is tested (the design's own
/// Test section names only that).
/// </summary>
public sealed class TrayService : IDisposable
{
    // Arbitrary but fixed - this app only ever shows one tray icon.
    private const uint IconId = 1;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_WARNING = 0x00000002;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    // Own range, distinct from GlobalHotkey's WM_HOTKEY handling on the main window - this class
    // owns a window of its own.
    private const int CallbackMessageId = 0x8000 + 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private const int SM_CXSMICON = 49;

    private readonly HwndSource _hwndSource;
    private readonly uint _taskbarCreatedMessage;
    private readonly ContextMenu _menu;
    private readonly WindowLayerMenu _windowLayerMenu;
    private readonly MenuItem _clickThroughItem;
    private readonly MenuItem _showHideItem;
    private readonly MenuItem _refreshItem;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _settingsItem;
    private readonly MenuItem _statsItem;
    private readonly MenuItem _resetPositionItem;
    private readonly MenuItem _exitItem;
    private readonly Icon _okIcon;
    private readonly Icon _warnIcon;
    private readonly Icon _critIcon;
    private readonly Icon _appIcon;
    // Icon(Stream) keeps reading from the stream for the icon's whole lifetime rather than decoding
    // it up front, so the resource stream LoadIcon opened cannot be disposed once the constructor
    // returns - each icon keeps its own backing MemoryStream alive alongside it instead, disposed
    // together with the icon in Dispose.
    private readonly MemoryStream _okIconStream;
    private readonly MemoryStream _warnIconStream;
    private readonly MemoryStream _critIconStream;
    private readonly MemoryStream _appIconStream;
    private readonly System.ComponentModel.PropertyChangedEventHandler _localizationChanged;
    private bool _suppressCheckEvents;
    private string _tooltipText = "AI-Usage";
    private UsageLevel? _lastLevel;
    private int? _lastPercent;
    private Icon? _renderedIcon;
    private IntPtr _renderedIconHandle;
    // Whatever icon is showing right now, rendered or one of the plain files - what an Explorer
    // restart puts back.
    private Icon? _currentIcon;
    private string? _hotkeyShortcutText;
    private DateTimeOffset? _pausedUntil;
    // False until the dispatcher's own startup burst has drained: a fresh tray icon can
    // receive a stray WM_RBUTTONUP the instant the message pump starts pumping - Explorer replaying
    // a queued click meant for whatever previously sat at this same taskbar slot, a known quirk right
    // after a restart, before the old instance's icon is fully gone. Before this flips true,
    // ShowContextMenu never runs, so the foreground call inside it (the one documented way to make
    // the popup close on an outside click) can never fire before a user could possibly have clicked
    // for real.
    private bool _readyForContextMenu;

    public event EventHandler? ShowHideRequested;
    public event EventHandler? RefreshRequested;
    /// <summary>The pause entry was picked: pauses fetching, or ends a running pause.</summary>
    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? StatsRequested;
    public event EventHandler? ResetPositionRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? WindowLayerChanged;
    public event EventHandler<bool>? ClickThroughChanged;

    public TrayService(string windowLayer, bool clickThrough)
    {
        (_okIcon, _okIconStream) = LoadIcon("tray-ok.ico");
        (_warnIcon, _warnIconStream) = LoadIcon("tray-warn.ico");
        (_critIcon, _critIconStream) = LoadIcon("tray-crit.ico");
        (_appIcon, _appIconStream) = LoadIcon("app.ico");

        // A zero-size, unstyled window whose only job is to receive the shell's tray callback and
        // (briefly, on a right-click) own the popup menu - never shown, never sized.
        _hwndSource = new HwndSource(new HwndSourceParameters("AI-Usage Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = IntPtr.Zero,
        });
        _hwndSource.AddHook(WndProc);
        // Explorer restarting (crash, "Restart Explorer" from Task Manager) drops every tray icon
        // silently; re-adding on this broadcast message is what NotifyIcon did for this app before.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var loc = LocalizationService.Instance;
        var parts = CreateMenu(windowLayer, clickThrough, hotkeyShortcutText: null, pausedUntil: null);
        _menu = parts.Menu;
        _showHideItem = parts.ShowHide;
        _refreshItem = parts.Refresh;
        _pauseItem = parts.Pause;
        _windowLayerMenu = parts.WindowLayer;
        _clickThroughItem = parts.ClickThrough;
        _settingsItem = parts.Settings;
        _statsItem = parts.Stats;
        _resetPositionItem = parts.ResetPosition;
        _exitItem = parts.Exit;

        _windowLayerMenu.Chosen += (_, layer) => WindowLayerChanged?.Invoke(this, layer);
        _clickThroughItem.Checked += (_, _) => RaiseCheckChanged(ClickThroughChanged, true);
        _clickThroughItem.Unchecked += (_, _) => RaiseCheckChanged(ClickThroughChanged, false);
        _showHideItem.Click += (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
        _refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _pauseItem.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _statsItem.Click += (_, _) => StatsRequested?.Invoke(this, EventArgs.Empty);
        _resetPositionItem.Click += (_, _) => ResetPositionRequested?.Invoke(this, EventArgs.Empty);
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        AddIcon();

        // Flips true only once the dispatcher has actually started pumping and drained whatever was
        // already queued for this hwnd - see the field comment on _readyForContextMenu. ApplicationIdle
        // sits below every real message (input, layout, rendering), so this never waits on anything a
        // user could have caused in the meantime; it only ever waits out the startup burst itself.
        _hwndSource.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _readyForContextMenu = true);

        // Menu items are only ever built once (this class lives for the whole
        // process) - a language switch must still reach them immediately, so this
        // re-reads all five texts on every LocalizationService change rather than only at startup.
        // Named so Dispose can unsubscribe it - the singleton outlives this instance otherwise and
        // keeps calling RefreshMenuText on already-disposed menu items.
        _localizationChanged = (_, _) => RefreshMenuText();
        loc.PropertyChanged += _localizationChanged;
    }

    /// <summary>Suppresses the Checked/Unchecked event while a caller-driven update
    /// (<see cref="UpdateClickThrough"/>) sets the checkbox, so that
    /// update never loops back out as a fake user click.</summary>
    private void RaiseCheckChanged(EventHandler<bool>? handler, bool value)
    {
        if (_suppressCheckEvents)
            return;
        handler?.Invoke(this, value);
    }

    private void RefreshMenuText() =>
        RefreshMenuText(
            new MenuParts(_menu, _showHideItem, _refreshItem, _pauseItem, _windowLayerMenu, _clickThroughItem,
                _settingsItem, _statsItem, _resetPositionItem, _exitItem),
            _hotkeyShortcutText, _pausedUntil);

    private static void RefreshMenuText(MenuParts parts, string? hotkeyShortcutText, DateTimeOffset? pausedUntil)
    {
        var loc = LocalizationService.Instance;
        parts.ShowHide.Header = loc["Tray.ShowHide"];
        parts.ShowHide.InputGestureText = hotkeyShortcutText ?? "";
        parts.Refresh.Header = loc["Action.RefreshNow"];
        parts.Pause.Header = pausedUntil is { } until
            ? loc.Format("Tray.Resume", TrayTooltipBuilder.ShortTime(until))
            : loc["Tray.Pause"];
        parts.WindowLayer.RefreshText();
        parts.ClickThrough.Header = loc["Tray.ClickThrough"];
        parts.Settings.Header = loc["Tray.Settings"];
        parts.Stats.Header = loc["Tray.Stats"];
        parts.ResetPosition.Header = loc["Tray.ResetPosition"];
        parts.Exit.Header = loc["Tray.Exit"];
    }

    private sealed record MenuParts(
        ContextMenu Menu, MenuItem ShowHide, MenuItem Refresh, MenuItem Pause, WindowLayerMenu WindowLayer, MenuItem ClickThrough,
        MenuItem Settings, MenuItem Stats, MenuItem ResetPosition, MenuItem Exit);

    /// <summary>The right-click menu with its texts in the current language and the two check marks
    /// set, without any click handler. Separate from the constructor so a tool can draw the menu
    /// without creating a notify icon.</summary>
    internal static ContextMenu BuildMenu(string windowLayer, bool clickThrough, string? hotkeyShortcutText,
        DateTimeOffset? pausedUntil = null) =>
        CreateMenu(windowLayer, clickThrough, hotkeyShortcutText, pausedUntil).Menu;

    private static MenuParts CreateMenu(string windowLayer, bool clickThrough, string? hotkeyShortcutText, DateTimeOffset? pausedUntil)
    {
        var parts = new MenuParts(
            new ContextMenu(),
            new MenuItem(),
            new MenuItem(),
            new MenuItem(),
            new WindowLayerMenu(windowLayer),
            new MenuItem { IsCheckable = true, IsChecked = clickThrough },
            new MenuItem(),
            new MenuItem(),
            new MenuItem(),
            new MenuItem());
        parts.Menu.Items.Add(parts.ShowHide);
        parts.Menu.Items.Add(parts.Refresh);
        parts.Menu.Items.Add(parts.Pause);
        parts.Menu.Items.Add(parts.WindowLayer.Header);
        parts.Menu.Items.Add(parts.ClickThrough);
        parts.Menu.Items.Add(parts.Settings);
        parts.Menu.Items.Add(parts.Stats);
        parts.Menu.Items.Add(parts.ResetPosition);
        parts.Menu.Items.Add(parts.Exit);
        RefreshMenuText(parts, hotkeyShortcutText, pausedUntil);
        return parts;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != CallbackMessageId)
            return IntPtr.Zero;

        switch (lParam.ToInt32())
        {
            case WM_RBUTTONUP:
                ShowContextMenu();
                handled = true;
                break;
            case WM_LBUTTONDBLCLK:
                ShowHideRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var (x, y) = NativeMonitors.CursorPosition();
        var toDips = _hwndSource.CompositionTarget.TransformFromDevice;
        var point = toDips.Transform(new Point(x, y));

        _menu.Placement = PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = point.X;
        _menu.VerticalOffset = point.Y;
        // Without the foreground the shell never tells this app about a click elsewhere, and the
        // menu stays open until one of its own entries is picked. Guarded by _readyForContextMenu
        // (see its field comment) so this can never run before a real click was even possible.
        if (_readyForContextMenu)
            SetForegroundWindow(_hwndSource.Handle);
        _menu.IsOpen = true;
    }

    /// <summary>Keeps the checked window level in sync when it was changed from the Settings window
    /// or the title bar menu instead of the tray menu itself.</summary>
    public void UpdateWindowLayer(string layer) => _windowLayerMenu.Select(layer);

    /// <summary>Keeps the tray checkbox in sync when click-through was changed from Settings or the
    /// global hotkey instead of the tray menu itself.</summary>
    public void UpdateClickThrough(bool value) => SetChecked(_clickThroughItem, value);

    private void SetChecked(MenuItem item, bool value)
    {
        _suppressCheckEvents = true;
        item.IsChecked = value;
        _suppressCheckEvents = false;
    }

    /// <summary>Switches the pause entry between "pause" and "resume (paused until ...)" when a pause
    /// starts or ends; null when no pause runs.</summary>
    public void UpdatePause(DateTimeOffset? pausedUntil)
    {
        _pausedUntil = pausedUntil;
        RefreshMenuText();
    }

    /// <summary>Names the active global shortcut (e.g. "Ctrl+Alt+U") in the "Anzeigen/Verstecken"
    /// entry itself - null while the hotkey is off or could not be registered, which drops back to
    /// the plain, unnamed entry.</summary>
    public void UpdateHotkeyShortcut(string? shortcutText)
    {
        _hotkeyShortcutText = shortcutText;
        RefreshMenuText();
    }

    /// <summary>Takes the already-built tooltip text rather than the raw provider lines - the
    /// caller (<c>MainWindow.TryComputeTraySummary</c>) already built it once to decide whether
    /// anything changed at all, and building it again here from the same lines would be pure
    /// waste.</summary>
    public void UpdateTooltip(string tooltipText)
    {
        _tooltipText = tooltipText;
        var data = NewNotifyIconData(NIF_TIP);
        data.szTip = _tooltipText;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Puts the highest level's colour on the tray icon, with <paramref name="percent"/>
    /// (that level's own rounded usage number) drawn on top of it - null falls back to the plain,
    /// number-less icon files (startup, or every tile hidden with nothing to show a number for).</summary>
    public void UpdateIcon(UsageLevel highestLevel, int? percent)
    {
        if (_lastLevel == highestLevel && _lastPercent == percent)
            return;

        _lastLevel = highestLevel;
        _lastPercent = percent;

        if (percent is not { } value)
        {
            AssignIcon(highestLevel switch
            {
                UsageLevel.Crit => _critIcon,
                UsageLevel.Warn => _warnIcon,
                _ => _okIcon,
            }, renderedHandle: null);
            return;
        }

        var size = TrayIconRenderer.IconSizeFor(GetSystemMetrics(SM_CXSMICON));
        using var bitmap = TrayIconRenderer.Render(highestLevel, value, size);
        var handle = bitmap.GetHicon();
        AssignIcon(Icon.FromHandle(handle), renderedHandle: handle);
    }

    /// <summary>Shows the plain program icon, without a number, and forgets the last level and
    /// number so the next <see cref="UpdateIcon"/> always draws again.</summary>
    public void ShowAppIcon()
    {
        _lastLevel = null;
        _lastPercent = null;
        if (ReferenceEquals(_currentIcon, _appIcon))
            return;

        AssignIcon(_appIcon, renderedHandle: null);
    }

    /// <summary>Assigns the tray's icon, then disposes and destroys whichever previously-rendered
    /// icon/handle this replaces - never before the new one is already showing. <c>Icon.FromHandle</c>
    /// does not take ownership of the handle, so a rendered icon's handle must be destroyed
    /// explicitly once it is safe to do so, or it leaks one GDI handle per refresh. The three
    /// loaded-from-resource fallback icons are never passed as <paramref name="renderedHandle"/> -
    /// they live for the lifetime of this instance and are disposed once, in <see cref="Dispose"/>.</summary>
    private void AssignIcon(Icon icon, IntPtr? renderedHandle)
    {
        var data = NewNotifyIconData(NIF_ICON);
        data.hIcon = icon.Handle;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
        _currentIcon = icon;

        var previousIcon = _renderedIcon;
        var previousHandle = _renderedIconHandle;
        _renderedIcon = renderedHandle is null ? null : icon;
        _renderedIconHandle = renderedHandle ?? IntPtr.Zero;

        if (previousIcon is null)
            return;
        previousIcon.Dispose();
        DestroyIcon(previousHandle);
    }

    /// <summary>A threshold warning as a balloon tip: the same shell mechanism
    /// <c>NotifyIcon.ShowBalloonTip</c> used to wrap, called directly.</summary>
    public void ShowBalloon(string text)
    {
        var data = NewNotifyIconData(NIF_INFO);
        data.szInfo = text;
        data.szInfoTitle = "AI-Usage";
        data.dwInfoFlags = NIIF_WARNING;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddIcon()
    {
        var data = NewNotifyIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.hIcon = (_currentIcon ?? _okIcon).Handle;
        data.szTip = _tooltipText;
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewNotifyIconData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwndSource.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessageId,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Copies the packed resource fully into an in-memory stream and hands that back
    /// alongside the icon built from it - <c>Icon(Stream)</c> reads from the stream lazily for as
    /// long as the icon is alive, so the original resource stream (which the caller must dispose)
    /// can never be the one kept open.</summary>
    private static (Icon Icon, MemoryStream Stream) LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{fileName}");
        var resource = Application.GetResourceStream(uri)
            ?? throw new FileNotFoundException($"Tray icon asset missing: Assets/{fileName}");
        var buffer = new MemoryStream();
        using (var resourceStream = resource.Stream)
            resourceStream.CopyTo(buffer);
        buffer.Position = 0;
        return (new Icon(buffer), buffer);
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= _localizationChanged;

        var data = NewNotifyIconData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();

        _okIcon.Dispose();
        _warnIcon.Dispose();
        _critIcon.Dispose();
        _appIcon.Dispose();
        _okIconStream.Dispose();
        _warnIconStream.Dispose();
        _critIconStream.Dispose();
        _appIconStream.Dispose();
        if (_renderedIcon is { } rendered)
        {
            rendered.Dispose();
            DestroyIcon(_renderedIconHandle);
        }
    }
}
