using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// The one hover tooltip every self-drawn chart in the statistics window now shows through - a real
/// WPF <see cref="Popup"/> (<see cref="Popup.AllowsTransparency"/> true) rather than text painted
/// straight into the host control's own <c>OnRender</c>. A drawn tooltip could never leave its own
/// control's clip rectangle, so a top-row cell in <see cref="StatsMonthGrid"/> or an edge bar in <see
/// cref="StatsBarChart"/> always clipped it at the control's, and so the window's, own border. A
/// popup is its own top-level window and is under no such limit.
/// </summary>
public sealed class ChartTooltipPopup
{
    private readonly Popup _popup;
    private readonly Border _border;
    private readonly TextBlock _textBlock;

    public ChartTooltipPopup()
    {
        _textBlock = new TextBlock { FontSize = 10 };
        _border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Child = _textBlock,
        };
        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.Custom,
            Child = _border,
        };
    }

    /// <summary>Whether the tooltip fits above <paramref name="anchorScreenTop"/> without its own top
    /// edge crossing <paramref name="screenTop"/> - the flip point between "above the pointer" (the
    /// ordinary case) and "below" (an anchor too close to the top of the screen, not just the host
    /// window, since the popup itself is now free to leave the window). Pure so the flip logic is unit
    /// testable without a live window.</summary>
    internal static bool FitsAbove(double anchorScreenTop, double tooltipHeight, double screenTop) =>
        anchorScreenTop - tooltipHeight >= screenTop;

    /// <summary>The flip test across unit spaces: the anchor and the work area top are physical
    /// pixels (what <c>PointToScreen</c> and the monitor query report), the tooltip height is a
    /// device-independent size, so it is scaled by <paramref name="dpiScale"/> before comparing.</summary>
    internal static bool FitsAboveOnMonitor(double anchorScreenTopPx, double tooltipHeightDip, double workAreaTopPx, double dpiScale) =>
        FitsAbove(anchorScreenTopPx, tooltipHeightDip * dpiScale, workAreaTopPx);

    private static bool FitsAboveScreenPoint(FrameworkElement owner, Point inOwner, double tooltipHeightDip)
    {
        var onScreen = owner.PointToScreen(inOwner);
        var scale = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var workTop = Services.NativeMonitors.WorkAreaTopAt(onScreen.X, onScreen.Y)
            ?? SystemParameters.VirtualScreenTop * scale;
        return FitsAboveOnMonitor(onScreen.Y, tooltipHeightDip, workTop, scale);
    }

    // The last pointer position (in the owner's coordinate space) when the tooltip follows the mouse;
    // null keeps it anchored to the bar or cell it was shown for.
    private Point? _mousePoint;

    /// <summary>Where the tooltip's top-left goes for a tooltip that follows the pointer: centered on
    /// the pointer, 12 above it, or 20 below it when <see cref="FitsAbove"/> says the pointer is too
    /// close to the top of the screen. Pure so the placement is unit testable without a live
    /// window.</summary>
    internal static Point PointerPlacement(Point pointer, Size tooltipSize, bool fitsAbove) =>
        new(pointer.X - tooltipSize.Width / 2, fitsAbove ? pointer.Y - tooltipSize.Height - 12 : pointer.Y + 20);

    /// <summary>Shows the tooltip anchored to <paramref name="anchorRect"/> (in <paramref
    /// name="owner"/>'s own coordinate space), centered on the anchor's own width, above it whenever
    /// <see cref="FitsAbove"/> allows, below it otherwise. With <paramref name="mousePoint"/> the
    /// tooltip sits at the pointer instead and follows it through <see cref="MoveTo"/>.</summary>
    public void Show(
        FrameworkElement owner, Rect anchorRect, IReadOnlyList<string> lines,
        Brush background, Brush textBrush, Brush borderBrush, Point? mousePoint = null)
    {
        _border.Background = background;
        _border.BorderBrush = borderBrush;
        _textBlock.Foreground = textBrush;
        _textBlock.Text = string.Join("\n", lines);
        _mousePoint = mousePoint;

        _popup.PlacementTarget = owner;
        _popup.CustomPopupPlacementCallback = (popupSize, _, _) =>
        {
            if (_mousePoint is { } pointer)
            {
                var location = PointerPlacement(pointer, popupSize, FitsAboveScreenPoint(owner, pointer, popupSize.Height + 12));
                return [new CustomPopupPlacement(location, PopupPrimaryAxis.None)];
            }

            var placeAbove = FitsAboveScreenPoint(owner, new Point(anchorRect.X, anchorRect.Y), popupSize.Height);

            var x = anchorRect.X + anchorRect.Width / 2 - popupSize.Width / 2;
            var y = placeAbove ? anchorRect.Y - popupSize.Height - 4 : anchorRect.Bottom + 4;
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        };

        _popup.IsOpen = true;
    }

    /// <summary>Moves a pointer-following tooltip to <paramref name="mousePoint"/>. WPF only asks the
    /// placement callback again when the popup's offsets change, so the horizontal offset is toggled
    /// by a hundredth of a pixel to force it.</summary>
    public void MoveTo(Point mousePoint)
    {
        if (!_popup.IsOpen)
            return;

        _mousePoint = mousePoint;
        _popup.HorizontalOffset = _popup.HorizontalOffset == 0 ? 0.01 : 0;
    }

    public void Hide() => _popup.IsOpen = false;
}
