namespace AiUsage.Views;

/// <summary>The width arithmetic of the settings window's two side grips, kept free of the window so
/// it can be tested. The width is clamped to the window's own limits and the left edge follows the
/// width that was really applied, so a drag past a limit can neither push the window off the screen
/// nor save an oversize width.</summary>
internal static class SettingsWindowResize
{
    /// <summary>The right grip: the width follows the drag, the left edge stays.</summary>
    internal static (double Width, double Left) FromRightEdge(
        double actualWidth, double left, double horizontalChange, double minWidth, double maxWidth) =>
        (Clamp(actualWidth + horizontalChange, minWidth, maxWidth), left);

    /// <summary>The left grip: the right edge stays, so the left edge moves by the width change.</summary>
    internal static (double Width, double Left) FromLeftEdge(
        double actualWidth, double left, double horizontalChange, double minWidth, double maxWidth)
    {
        var width = Clamp(actualWidth - horizontalChange, minWidth, maxWidth);
        return (width, left + (actualWidth - width));
    }

    private static double Clamp(double width, double minWidth, double maxWidth) =>
        Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth));
}
