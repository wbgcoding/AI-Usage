using System.Windows.Automation;
using System.Windows.Interop;
using AiUsage.Services;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>
/// The window behind the tray popup: borderless, topmost, no taskbar entry, never activated, so it
/// never takes the focus from whatever the user is working in. It is positioned in physical pixels
/// with <c>SetWindowPos</c> (see <see cref="TrayPopupPlacement"/>) because a WPF Left/Top is in the
/// DPI of whichever monitor the window happens to be on, which is not the one under the icon.
/// </summary>
internal sealed class TrayPopupWindow : Window
{
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly TrayPopupView _view = new();
    private System.Windows.Point _anchor;

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

    /// <summary>Shows the popup for <paramref name="model"/> next to the icon point
    /// (<paramref name="anchorX"/>, <paramref name="anchorY"/> in physical screen pixels), or, when
    /// it is already open, replaces its content and puts it where it belongs again.</summary>
    internal void ShowAt(TrayPopupModel model, int anchorX, int anchorY)
    {
        _anchor = new System.Windows.Point(anchorX, anchorY);
        Present(model);
    }

    /// <summary>Replaces the content of an open popup; a closed one stays closed.</summary>
    internal void Refresh(TrayPopupModel model)
    {
        if (IsVisible)
            Present(model);
    }

    internal void HidePopup()
    {
        if (IsVisible)
            Hide();
    }

    private void Present(TrayPopupModel model)
    {
        _view.DataContext = model;
        _view.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _view.DesiredSize;

        var handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Win32.GetWindowLongPtr(handle, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(handle, Win32.GwlExStyle, new IntPtr(style | WsExToolWindow | WsExNoActivate));

        var monitor = NativeMonitors.MonitorAt((int)_anchor.X, (int)_anchor.Y);
        var placement = TrayPopupPlacement.Place(_anchor, monitor.WorkArea, size, monitor.Scale);

        // The first move brings the window to the right monitor, so a scaling change is handled by
        // WPF before the final size is set; the second one then sticks.
        MoveTo(handle, placement);
        if (!IsVisible)
            Show();
        MoveTo(handle, placement);
    }

    private static void MoveTo(IntPtr handle, PopupPlacement placement) =>
        Win32.SetWindowPos(handle, HwndTopmost, (int)placement.X, (int)placement.Y, (int)placement.Width, (int)placement.Height,
            Win32.SwpNoActivate);
}
