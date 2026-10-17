using System.Windows;
using System.Windows.Threading;
using AiUsage.Services;

namespace AiUsage.Views;

// Window placement: re-placing the widget after a DPI or monitor change, deferred while a drag runs.
public partial class MainWindow
{
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private readonly ReplaceAfterDrag _replaceAfterDrag = new();

    /// <summary>The display setup the window was last placed for; a different one on a display change
    /// means the remembered place for the new setup applies.</summary>
    private string _lastSetupKey = "";

    /// <summary>Records where the window stands as its place for the current display setup. Only
    /// deliberate moves call this (end of a drag, snap, reset): Windows shuffling the window during a
    /// display change must not overwrite the place remembered for the setup it left.</summary>
    private void RememberSetupPlace()
    {
        _lastSetupKey = WindowPlacementService.SetupKey(_monitorAreas.Areas);
        WindowPlacementService.RememberSetup(_settings.Window, _lastSetupKey, Left, Top, _settings.Window.MonitorDeviceName);
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>A monitor going away (a dock unplugged, a display turned off) must pull the window
    /// back onto whatever is left right away - not just fix it at the next start.</summary>
    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ReplaceOnScreen);

    /// <summary>Moving to a monitor with another scale factor changes the window's size in device
    /// independent units; the position is resolved again once layout has settled. A change that
    /// arrives while the user drags the window (the DPI changes exactly as it crosses the seam
    /// between two monitors) waits for the drop, so the widget does not jump under the pointer.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_replaceAfterDrag.Request())
                ReplaceOnScreen();
        });
    }

    /// <summary>Decides when a re-placement may run relative to a drag of the window: at once when
    /// none is going on, otherwise once at its end.</summary>
    internal sealed class ReplaceAfterDrag
    {
        private bool _dragging;
        private bool _pending;

        public void DragStarted() => _dragging = true;

        /// <summary>True when the caller may re-place now; false when the request was kept for the
        /// end of the drag.</summary>
        public bool Request()
        {
            if (!_dragging)
                return true;
            _pending = true;
            return false;
        }

        /// <summary>True when a request is waiting that the caller has to run now.</summary>
        public bool DragEnded()
        {
            _dragging = false;
            var pending = _pending;
            _pending = false;
            return pending;
        }
    }

    /// <summary>Puts the window back inside what the current monitors offer; on a display setup it has
    /// a remembered place for (and was not placed for just now), it goes there first.</summary>
    private void ReplaceOnScreen()
    {
        _monitorAreas.Refresh();

        var monitors = _monitorAreas.Areas;
        var primary = WindowPlacementService.PrimaryOf(monitors);
        var current = new WindowRect(Left, Top, Width, Math.Max(ActualHeight, MinHeight));
        var key = WindowPlacementService.SetupKey(monitors);
        if (key != _lastSetupKey && WindowPlacementService.FindSetup(_settings.Window, key) is { } place)
        {
            current = current with { Left = place.Left, Top = place.Top };
            primary = monitors.FirstOrDefault(m => m.DeviceName == place.MonitorDeviceName, primary);
        }

        _lastSetupKey = key;
        var resolved = WindowPlacementService.ResolvePosition(current, monitors, primary);
        Left = resolved.Left;
        Top = resolved.Top;
    }
}
