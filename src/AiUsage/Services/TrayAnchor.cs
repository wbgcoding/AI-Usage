using System.Globalization;
using System.Windows;

namespace AiUsage.Services;

/// <summary>Where the anchor point of a tray popup came from.</summary>
public enum TrayAnchorSource
{
    /// <summary>The icon's own rectangle (<c>Shell_NotifyIconGetRect</c>).</summary>
    Icon,
    /// <summary>The point the shell put in the callback's <c>wParam</c>.</summary>
    WParam,
    /// <summary>The mouse cursor, when neither of the others was usable.</summary>
    Cursor,
}

/// <summary>The side of a monitor the taskbar sits on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>The resolved anchor: a point in physical screen pixels, where it came from, and the icon
/// rectangle when the shell gave one.</summary>
public readonly record struct TrayAnchor(Point Point, TrayAnchorSource Source, Rect? IconRect);

/// <summary>
/// Decides which point the popup (and the context menu) hangs on, and which side of the monitor the
/// taskbar is on. Pure geometry in physical pixels. The point the shell sends in the callback has been
/// seen to be a small value such as (1, 0) on a real desktop, so it only counts when the icon rectangle
/// confirms it, or when there is no rectangle to check against.
/// </summary>
public static class TrayAnchorResolver
{
    /// <summary>The callback point when it lies inside the icon rectangle, the rectangle's centre when
    /// the point is missing or outside it, the callback point when there is no rectangle, the cursor
    /// when there is neither. An empty rectangle counts as none, a (0, 0) point as none.</summary>
    public static TrayAnchor Resolve(Rect? iconRect, Point? callbackPoint, Point cursor)
    {
        Rect? rect = iconRect is { IsEmpty: false, Width: > 0, Height: > 0 } r ? r : null;
        Point? wParam = callbackPoint is { } p && (p.X != 0 || p.Y != 0) ? p : null;

        if (rect is { } icon)
        {
            if (wParam is { } inside && icon.Contains(inside))
                return new TrayAnchor(inside, TrayAnchorSource.WParam, icon);
            return new TrayAnchor(new Point(Math.Floor(icon.Left + icon.Width / 2), Math.Floor(icon.Top + icon.Height / 2)),
                TrayAnchorSource.Icon, icon);
        }

        return wParam is { } point
            ? new TrayAnchor(point, TrayAnchorSource.WParam, null)
            : new TrayAnchor(cursor, TrayAnchorSource.Cursor, null);
    }

    /// <summary>The edge the taskbar is on: the side of the work area the icon centre lies outside of;
    /// when the icon sits inside the work area (a flyout of hidden icons, an auto-hide taskbar), the
    /// side where the monitor is larger than its work area; with neither, the monitor edge nearest the
    /// icon.</summary>
    public static TaskbarEdge DetectEdge(Point iconCentre, Rect monitorBounds, Rect workArea)
    {
        if (iconCentre.Y > workArea.Bottom)
            return TaskbarEdge.Bottom;
        if (iconCentre.Y < workArea.Top)
            return TaskbarEdge.Top;
        if (iconCentre.X < workArea.Left)
            return TaskbarEdge.Left;
        if (iconCentre.X > workArea.Right)
            return TaskbarEdge.Right;

        var bottom = monitorBounds.Bottom - workArea.Bottom;
        var top = workArea.Top - monitorBounds.Top;
        var left = workArea.Left - monitorBounds.Left;
        var right = monitorBounds.Right - workArea.Right;
        var biggest = Math.Max(Math.Max(bottom, top), Math.Max(left, right));
        if (biggest > 0)
        {
            if (bottom == biggest)
                return TaskbarEdge.Bottom;
            if (top == biggest)
                return TaskbarEdge.Top;
            return left == biggest ? TaskbarEdge.Left : TaskbarEdge.Right;
        }

        var toBottom = monitorBounds.Bottom - iconCentre.Y;
        var toTop = iconCentre.Y - monitorBounds.Top;
        var toLeft = iconCentre.X - monitorBounds.Left;
        var toRight = monitorBounds.Right - iconCentre.X;
        var nearest = Math.Min(Math.Min(toBottom, toTop), Math.Min(toLeft, toRight));
        if (toBottom == nearest)
            return TaskbarEdge.Bottom;
        if (toTop == nearest)
            return TaskbarEdge.Top;
        return toLeft == nearest ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>The one log line written per popup open.</summary>
    public static string FormatLog(TrayAnchor anchor, Rect workArea, Size sizePx, Point placed)
    {
        static string N(double value) => Math.Floor(value + 0.5).ToString(CultureInfo.InvariantCulture);
        var source = anchor.Source switch
        {
            TrayAnchorSource.Icon => "icon",
            TrayAnchorSource.WParam => "wparam",
            _ => "cursor",
        };
        var icon = anchor.IconRect is { } r ? $"{N(r.Left)},{N(r.Top)},{N(r.Right)},{N(r.Bottom)}" : "none";
        return $"Tray popup: anchor {N(anchor.Point.X)},{N(anchor.Point.Y)} (source {source}), icon rect {icon}, "
            + $"work area {N(workArea.Left)},{N(workArea.Top)},{N(workArea.Right)},{N(workArea.Bottom)}, "
            + $"size {N(sizePx.Width)}x{N(sizePx.Height)}, placed {N(placed.X)},{N(placed.Y)}";
    }
}
