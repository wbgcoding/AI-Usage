using System.Windows;
using System.Windows.Interop;

namespace AiUsage.Services;

/// <summary>The edge arithmetic of the windows that resize with their own grips, and the one call that
/// applies a new position and size together. Setting Left/Top and then Width/Height shows the window
/// moved at its old size for a frame, so its content jumps while a top or left edge is dragged; a
/// single native placement call has no such frame. The size is clamped to the window's own limits and
/// the moving edge follows the size that was really applied, so a drag past a limit can neither push
/// the window off the screen nor save an oversize value.</summary>
internal static class WindowEdgeResize
{
    /// <summary>The right or bottom grip: the size follows the drag, the position stays.</summary>
    internal static (double Size, double Position) FromFarEdge(
        double size, double position, double change, double minSize, double maxSize) =>
        (Clamp(size + change, minSize, maxSize), position);

    /// <summary>The left or top grip: the opposite edge stays, so the position moves by the size change.</summary>
    internal static (double Size, double Position) FromNearEdge(
        double size, double position, double change, double minSize, double maxSize)
    {
        var newSize = Clamp(size - change, minSize, maxSize);
        return (newSize, AnchorFarEdge(position, size, newSize));
    }

    /// <summary>The position that keeps the far edge (right or bottom) where it was when the size changes.</summary>
    internal static double AnchorFarEdge(double position, double size, double newSize) =>
        position + (size - newSize);

    /// <summary>True when every value is a real number, so one native call can place the window; a
    /// window that still sizes itself reports NaN for that side and is placed by property instead.</summary>
    internal static bool CanPlaceNatively(params double[] values) =>
        values.All(value => !double.IsNaN(value) && !double.IsInfinity(value));

    /// <summary>Places and sizes the window in one native call; falls back to the properties when the
    /// window has no handle yet or one of the values is not a number.</summary>
    internal static void MoveAndResize(Window window, double left, double top, double width, double height)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero
            || !CanPlaceNatively(left, top, width, height)
            || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
        {
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            return;
        }

        var toDevice = target.TransformToDevice;
        var origin = toDevice.Transform(new Point(left, top));
        var size = toDevice.Transform(new Vector(width, height));
        Win32.SetWindowPos(handle, IntPtr.Zero, Convert.ToInt32(origin.X), Convert.ToInt32(origin.Y),
            Convert.ToInt32(size.X), Convert.ToInt32(size.Y), Win32.SwpNoZOrder | Win32.SwpNoActivate);
    }

    private static double Clamp(double size, double minSize, double maxSize) =>
        Math.Clamp(size, minSize, Math.Max(minSize, maxSize));
}
