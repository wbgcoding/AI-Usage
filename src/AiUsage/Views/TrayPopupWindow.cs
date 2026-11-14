using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsage.Services;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>
/// The window behind the tray popup: borderless, topmost, no taskbar entry, never activated, so it
/// never takes the focus from whatever the user is working in. The target position is worked out in
/// physical pixels (see <see cref="TrayPopupPlacement"/>), applied with <c>SetWindowPos</c> and set on
/// the WPF Left/Top/Width/Height in device independent units of the target monitor as well, so WPF's
/// own layout agrees with it. WPF can still move the window after the first Show (its layout pass runs
/// later), so the position is checked again on the next dispatcher passes and put back when it differs.
/// </summary>
internal sealed class TrayPopupWindow : Window
{
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private const int MaxChecks = 3;
    private const int TolerancePx = 1;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly TrayPopupView _view = new();
    private TrayAnchor _anchor;
    private PopupPlacement _target;
    private int _generation;

    public TrayPopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        Content = _view;
        AutomationProperties.SetName(this, AppInfo.ProductName);
        MouseDown += (_, _) => HidePopup();
    }

    /// <summary>Whether the popup is on screen.</summary>
    internal bool IsShown => IsVisible;

    internal TrayPopupView View => _view;

    /// <summary>Shows the popup for <paramref name="model"/> next to the icon (<paramref name="anchor"/>,
    /// physical screen pixels), or, when it is already open, replaces its content and puts it where it
    /// belongs again. Writes one log line with what it did.</summary>
    internal void ShowAt(TrayPopupModel model, TrayAnchor anchor)
    {
        _anchor = anchor;
        var work = Present(model);
        LogService.Shared.LogInfo(TrayAnchorResolver.FormatLog(
            anchor, work, new System.Windows.Size(_target.Width, _target.Height), new System.Windows.Point(_target.X, _target.Y)));
    }

    /// <summary>Replaces the content of an open popup; a closed one stays closed.</summary>
    internal void Refresh(TrayPopupModel model)
    {
        if (IsVisible)
            Present(model);
    }

    internal void HidePopup()
    {
        _generation++;
        if (IsVisible)
            Hide();
    }

    private System.Windows.Rect Present(TrayPopupModel model)
    {
        _view.DataContext = model;
        _view.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _view.DesiredSize;

        var handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Win32.GetWindowLongPtr(handle, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(handle, Win32.GwlExStyle, new IntPtr(style | WsExToolWindow | WsExNoActivate));

        var (bounds, work, scale) = NativeMonitors.MonitorGeometryAt((int)_anchor.Point.X, (int)_anchor.Point.Y);
        _target = TrayPopupPlacement.Place(_anchor, bounds, work, size, scale);
        var generation = ++_generation;

        // The first move brings the window to the right monitor, so a scaling change is handled by
        // WPF before the final size is set; the second one then sticks.
        MoveTo(handle, _target);
        SetWpfBounds(_target, scale);
        if (!IsVisible)
            Show();
        MoveTo(handle, _target);

        // WPF applies its own layout after Show; look again once it had its turn.
        ScheduleCheck(handle, generation, MaxChecks, DispatcherPriority.Loaded);
        return work;
    }

    private void SetWpfBounds(PopupPlacement placement, double scale)
    {
        if (scale <= 0)
            scale = 1;
        Left = placement.X / scale;
        Top = placement.Y / scale;
        Width = placement.Width / scale;
        Height = placement.Height / scale;
    }

    private void ScheduleCheck(IntPtr handle, int generation, int remaining, DispatcherPriority priority) =>
        Dispatcher.BeginInvoke(priority, () =>
        {
            if (generation != _generation || !IsVisible)
                return;
            if (IsAtTarget(handle))
                return;

            // The window was moved or resized behind the placement's back: put it back.
            if (remaining == MaxChecks)
                LogService.Shared.LogInfo($"Tray popup: the window had moved, put back at {_target.X:0},{_target.Y:0}.");
            MoveTo(handle, _target);
            if (remaining > 1)
                ScheduleCheck(handle, generation, remaining - 1, DispatcherPriority.ContextIdle);
        });

    private bool IsAtTarget(IntPtr handle)
    {
        if (!Win32.GetWindowRect(handle, out var rect))
            return true;
        return Math.Abs(rect.Left - _target.X) <= TolerancePx
            && Math.Abs(rect.Top - _target.Y) <= TolerancePx
            && Math.Abs(rect.Right - rect.Left - _target.Width) <= TolerancePx
            && Math.Abs(rect.Bottom - rect.Top - _target.Height) <= TolerancePx;
    }

    private static void MoveTo(IntPtr handle, PopupPlacement placement) =>
        Win32.SetWindowPos(handle, HwndTopmost, (int)placement.X, (int)placement.Y, (int)placement.Width, (int)placement.Height,
            Win32.SwpNoActivate);
}
