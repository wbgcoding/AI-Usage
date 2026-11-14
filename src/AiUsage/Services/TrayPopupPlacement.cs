namespace AiUsage.Services;

/// <summary>Where the popup goes and how large it is, in physical screen pixels.</summary>
public readonly record struct PopupPlacement(double X, double Y, double Width, double Height);

/// <summary>
/// Puts the tray popup next to the icon: above it when the icon is in the work area or in a bottom
/// taskbar, below it for a top taskbar, beside it for a taskbar on the left or right, always inside
/// the work area of the monitor the icon is on. Pure geometry in physical pixels; the popup's size
/// comes in device independent units and the scale of that monitor turns it into pixels, so a monitor
/// with another scaling gets its own pixel size.
/// </summary>
public static class TrayPopupPlacement
{
    /// <summary>The distance in DIPs kept between the icon point and the popup.</summary>
    public const double Gap = 8;

    public static PopupPlacement Place(System.Windows.Point anchorPx, System.Windows.Rect workAreaPx, System.Windows.Size sizeDips, double scale)
    {
        if (scale <= 0)
            scale = 1;
        var width = Math.Min(Math.Ceiling(sizeDips.Width * scale), workAreaPx.Width);
        var height = Math.Min(Math.Ceiling(sizeDips.Height * scale), workAreaPx.Height);
        var gap = Whole(Gap * scale);

        double x;
        double y;
        if (anchorPx.X < workAreaPx.Left)
        {
            // Taskbar on the left: beside it, centred on the icon.
            x = workAreaPx.Left + gap;
            y = anchorPx.Y - height / 2;
        }
        else if (anchorPx.X > workAreaPx.Right)
        {
            x = workAreaPx.Right - width - gap;
            y = anchorPx.Y - height / 2;
        }
        else
        {
            x = anchorPx.X - width / 2;
            if (anchorPx.Y < workAreaPx.Top)
                y = workAreaPx.Top + gap;
            else if (anchorPx.Y > workAreaPx.Bottom)
                y = workAreaPx.Bottom - height - gap;
            else
            {
                y = anchorPx.Y - height - gap;
                if (y < workAreaPx.Top)
                    y = anchorPx.Y + gap;
            }
        }

        x = Math.Clamp(x, workAreaPx.Left, workAreaPx.Right - width);
        y = Math.Clamp(y, workAreaPx.Top, workAreaPx.Bottom - height);
        return new PopupPlacement(Whole(x), Whole(y), width, height);
    }

    // Whole device pixels; not a usage percentage, so not the shared percentage rounding.
    private static double Whole(double value) => Math.Floor(value + 0.5);
}
