using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Services;

/// <summary>A monitor's usable area (excludes the taskbar), device-independent units.</summary>
public readonly record struct MonitorArea(string DeviceName, double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public readonly record struct WindowRect(double Left, double Top, double Width, double Height);

public enum TileDensity { Full, Mini }

/// <summary>One of the four halves a window can snap to, mirroring Win+Arrow - the shell keeps that
/// exact key combination for itself, so the app offers the same capability under a different one.</summary>
public enum SnapDirection { Left, Right, Up, Down }

public readonly record struct HeightResolution(TileDensity Density, double WindowHeight, bool NeedsScroll);

/// <summary>
/// Pure placement math for the frameless main window: where the window lands
/// relative to the monitors it remembers, and how its automatic height reacts when the desired
/// content does not fit the screen. No real window or Win32 call here - MainWindow.xaml.cs supplies
/// the monitor rectangles (from System.Windows.Forms.Screen) and calls into this. All of that stays
/// static, called the same way it always was; the one instance member below
/// (<see cref="RememberedStatsWindowSize"/>) is the sole exception - it needs a live
/// <see cref="AppSettings"/> to read and write through, so the class itself is no longer declared
/// "static" (an ordinary class with only static callers behaves identically to one), but nothing
/// about the static API above changed.
/// </summary>
public class WindowPlacementService
{
    private const double TitleBarHeight = 32;
    private const double ChromeHeight = 8;

    /// <summary>A single tile's own width, the width its layout was drawn for.</summary>
    public const double TileWidth = 340;

    /// <summary>Air between two tiles standing side by side.</summary>
    public const double TileGap = 4;

    /// <summary>The tile list's own left and right padding plus the window border.</summary>
    public const double ChromeWidth = 18;

    /// <summary>Transparent gutter around the visible chrome that the drop shadow draws into.</summary>
    public const double ShadowMargin = 8;

    /// <summary>Never let the window run right up to the edge of the screen.</summary>
    public const double EdgeMargin = 16;

    public const double MinWindowWidth = 300;

    /// <summary>Mirrors MainWindow.xaml's Window.MinHeight.</summary>
    public const double MinWindowHeight = 88;

    private readonly AppSettings _settings;
    private readonly SettingsStore? _store;

    /// <summary>Built on the one <see cref="AppSettings"/> instance the whole process shares and
    /// saves through (see <see cref="Shared"/>) - <paramref name="store"/> is optional so a test can
    /// build an instance on a bare settings object with no disk I/O at all, proving persistence
    /// survives a fresh instance the same way a real restart (settings.json reloaded) would.</summary>
    public WindowPlacementService(AppSettings settings, SettingsStore? store = null)
    {
        _settings = settings;
        _store = store;
    }

    /// <summary>The one instance every window in the process reads and writes through - set once
    /// from App.xaml.cs's OnStartup with the same settings instance and store every other window
    /// already mutates and saves in place. StatsWindow.xaml.cs has no constructor path back to
    /// App.xaml.cs (MainWindow.xaml.cs's sole "new StatsWindow(...)" call site takes no settings
    /// argument), so this static seam is what it reads instead.</summary>
    public static WindowPlacementService? Shared { get; set; }

    /// <summary>The Stats window's own remembered size, persisted into <see cref="AppSettings"/>
    /// (<see cref="WindowSettings.StatsWidth"/>/<see cref="WindowSettings.StatsHeight"/>) - 0 in
    /// either one means "never resized", read back as null here rather than as a degenerate size.</summary>
    public (double Width, double Height)? RememberedStatsWindowSize
    {
        get => _settings.Window.StatsWidth > 0 && _settings.Window.StatsHeight > 0
            ? (_settings.Window.StatsWidth, _settings.Window.StatsHeight)
            : null;
        set
        {
            _settings.Window.StatsWidth = value is { } size ? SettingsRanges.ClampStatsWindowWidth(size.Width) : 0;
            _settings.Window.StatsHeight = value is { } size2 ? SettingsRanges.ClampStatsWindowHeight(size2.Height) : 0;
            _store?.RequestSave(_settings);
        }
    }

    private static readonly TileDensity[] DensityOrder = [TileDensity.Full, TileDensity.Mini];

    /// <summary>
    /// Resolves a remembered window rectangle against the monitors that exist now: unchanged if it
    /// fully fits one of them, clamped into the nearest one if it only partially overlaps, or
    /// centered on <paramref name="primary"/> if it does not overlap any monitor at all.
    /// </summary>
    public static WindowRect ResolvePosition(WindowRect desired, IReadOnlyList<MonitorArea> monitors, MonitorArea primary)
    {
        foreach (var monitor in monitors)
        {
            if (FullyContains(monitor, desired))
                return desired;
        }

        foreach (var monitor in monitors)
        {
            if (Overlaps(monitor, desired))
                return ClampIntoArea(desired, monitor);
        }

        return CenterOn(primary, desired.Width, desired.Height);
    }

    /// <summary>
    /// Automatic-height mode: starts from <paramref name="preferredDensity"/>
    /// (the settings-chosen stage, normally Full) and steps down one stage at a time until the whole
    /// window fits the monitor's work area; if even Mini does not fit, the height is capped to the
    /// work area and the caller must show a scrollbar.
    /// </summary>
    public static HeightResolution ResolveAutomaticHeight(int visibleTileCount, TileDensity preferredDensity, double workAreaHeight)
    {
        var startIndex = Array.IndexOf(DensityOrder, preferredDensity);
        for (var i = startIndex; i < DensityOrder.Length; i++)
        {
            var density = DensityOrder[i];
            var height = ContentHeight(visibleTileCount, density);
            if (height <= workAreaHeight)
                return new HeightResolution(density, height, NeedsScroll: false);
        }

        return new HeightResolution(TileDensity.Mini, workAreaHeight, NeedsScroll: true);
    }

    /// <summary>
    /// The width the window needs so that every visible tile gets its full width: one tile wide when
    /// the tiles are stacked, all of them side by side when they stand in a row. Capped to the work
    /// area, so four tiles on a small screen produce a scrollable row rather than a window running
    /// off the edge.
    /// </summary>
    /// <summary>
    /// Keeps a window that sizes itself to its content inside the work area. The cap is the whole
    /// work area, and a window that has grown taller than the space below it slides up instead of
    /// hanging behind the taskbar - a tile can always turn out taller than any height model assumed.
    /// </summary>
    public static (double Top, double MaxHeight) FitVertically(double top, double contentHeight, MonitorArea area)
    {
        var height = Math.Clamp(contentHeight, 0, area.Height);
        var lowestTop = Math.Max(area.Top, area.Bottom - height);
        return (Math.Clamp(top, area.Top, lowestTop), area.Height);
    }

    public static double ResolveContentWidth(int visibleTiles, bool horizontal, double workAreaWidth, double zoom = 1)
    {
        var tiles = Math.Max(1, visibleTiles);
        var content = horizontal ? tiles * TileWidth + (tiles - 1) * TileGap : TileWidth;
        var desired = (content + ChromeWidth + 2 * ShadowMargin) * zoom;
        var minWidth = MinWidthFor(zoom);
        var available = Math.Max(minWidth, workAreaWidth - EdgeMargin);
        return Math.Clamp(desired, minWidth, available);
    }

    /// <summary>Manual-height mode: a stored dragged height is capped to whatever work area exists now.</summary>
    public static double ResolveManualHeight(double storedHeight, double workAreaHeight) =>
        Math.Min(storedHeight, workAreaHeight);

    /// <summary>The Stats window's own opening size: the remembered size if it still fits inside the
    /// monitor it is about to open on, otherwise the fixed default - the same "falls back once it no
    /// longer fits" rule <see cref="ResolvePosition"/> applies to a remembered position.</summary>
    public static (double Width, double Height) ResolveStatsWindowSize(
        (double Width, double Height)? remembered, MonitorArea area, double defaultWidth, double defaultHeight)
    {
        if (remembered is { } size && size.Width <= area.Width && size.Height <= area.Height)
            return size;
        return (defaultWidth, defaultHeight);
    }

    /// <summary>The window's MinHeight floor: 0 while collapsed, so SizeToContent can shrink all the
    /// way down to just the title bar with no leftover strip of background; the normal floor once
    /// restored.</summary>
    public static double MinHeightFor(bool collapsed, double zoom = 1) => collapsed ? 0 : MinWindowHeight * zoom;

    /// <summary>The height a restore from the collapsed state should produce. In automatic-height
    /// mode SizeToContent recomputes the height on its own, so there is nothing to set here - null
    /// tells the caller to leave it to SizeToContent rather than fight it with an explicit value. In
    /// manual mode it is the height captured right before collapsing, falling back to whatever manual
    /// height was already remembered on the rare chance that capture never happened.</summary>
    public static double? RestoreHeight(double uncollapsedHeight, double? manualHeight, bool automatic) =>
        automatic ? null : (uncollapsedHeight > 0 ? uncollapsedHeight : manualHeight);

    /// <summary>The tallest a manually resized window should be allowed to grow: the content's own
    /// desired height plus the title bar and chrome around it. Growing past that only pulls empty
    /// space in below the last tile - dragging the bottom edge should stop exactly where the content
    /// ends.</summary>
    public static double ClampToContentHeight(double desiredHeight, double contentDesiredHeight, double zoom = 1) =>
        Math.Min(desiredHeight, (contentDesiredHeight + TitleBarHeight + ChromeHeight) * zoom);

    /// <summary>The bottom edge's own drag math: <paramref name="currentHeight"/> plus
    /// <paramref name="delta"/>, floored at <paramref name="minHeight"/>, clamped by
    /// <see cref="ClampToContentHeight"/> so the window still never grows past its own tiles, and
    /// finally capped at <paramref name="workAreaHeight"/> - always the CURRENT monitor's work area,
    /// never whatever <c>Window.MaxHeight</c> was last pinned to while the height was automatic. A
    /// drag pulling the window small used to leave that stale ceiling in place for the rest of the
    /// manual session, so growing back afterwards silently stopped at it instead of the mouse -
    /// this recomputes the ceiling fresh on every call instead of trusting a value that might be
    /// stale by now.</summary>
    public static double GrowManualHeight(double currentHeight, double delta, double minHeight, double workAreaHeight, double contentDesiredHeight, double zoom = 1)
    {
        var raised = Math.Max(minHeight, currentHeight + delta);
        var contentClamped = ClampToContentHeight(raised, contentDesiredHeight, zoom);
        return Math.Min(contentClamped, workAreaHeight);
    }

    /// <summary>
    /// Windows' own edge snapping never applies to a layered, frameless window, so a hand-dragged
    /// widget is never quite flush with the screen. Each of the four edges is tested independently
    /// against <paramref name="window"/>'s ORIGINAL position - never against an edge this same call
    /// already moved - so a window wider than the monitor can snap its left edge without the (still
    /// far away) right edge dragging it back. Position only; the size never changes.
    /// </summary>
    public static WindowRect SnapToEdges(WindowRect window, MonitorArea area, double tolerance = 12)
    {
        var left = window.Left;
        if (Math.Abs(window.Left - area.Left) <= tolerance)
            left = area.Left;
        else if (Math.Abs(window.Left + window.Width - area.Right) <= tolerance)
            left = area.Right - window.Width;

        var top = window.Top;
        if (Math.Abs(window.Top - area.Top) <= tolerance)
            top = area.Top;
        else if (Math.Abs(window.Top + window.Height - area.Bottom) <= tolerance)
            top = area.Bottom - window.Height;

        return window with { Left = left, Top = top };
    }

    /// <summary>Places the window on exactly one half of <paramref name="area"/> - the same geometry
    /// Windows' own Win+Arrow snap would use, since <see cref="SnapDirection"/> only exists because
    /// that combination itself cannot be adopted (the shell reserves it).</summary>
    public static WindowRect SnapToHalf(MonitorArea area, SnapDirection direction) => direction switch
    {
        SnapDirection.Left => new WindowRect(area.Left, area.Top, area.Width / 2, area.Height),
        SnapDirection.Right => new WindowRect(area.Left + area.Width / 2, area.Top, area.Width / 2, area.Height),
        SnapDirection.Up => new WindowRect(area.Left, area.Top, area.Width, area.Height / 2),
        SnapDirection.Down => new WindowRect(area.Left, area.Top + area.Height / 2, area.Width, area.Height / 2),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static double ContentHeight(int visibleTileCount, TileDensity density) =>
        TitleBarHeight + ChromeHeight + visibleTileCount * TileDensitySelector.HeightFor(density);

    /// <summary>Title bar plus window chrome, for sizing before the window has been laid out once
    /// and its real non-content height can be read off.</summary>
    public static double DefaultChromeHeight => TitleBarHeight + ChromeHeight;

    /// <summary><see cref="DefaultChromeHeight"/> at <paramref name="zoom"/>.</summary>
    public static double ChromeHeightFor(double zoom) => DefaultChromeHeight * zoom;

    /// <summary>The narrowest the window may be at <paramref name="zoom"/>.</summary>
    public static double MinWidthFor(double zoom) => MinWindowWidth * zoom;

    /// <summary>How near an edge a dropped window snaps to it, at <paramref name="zoom"/>.</summary>
    public static double SnapToleranceFor(double zoom) => 12 * zoom;

    /// <summary>The remembered sizes after the zoom changed by <paramref name="ratio"/> (new zoom over old):
    /// widths always, heights only where the person set one. Position and the other windows' sizes stay.</summary>
    public static void ScaleRememberedSizes(WindowSettings window, double ratio)
    {
        foreach (var sizes in new[] { window.Vertical, window.Horizontal })
        {
            sizes.Width *= ratio;
            if (sizes.Height is { } height)
                sizes.Height = height * ratio;
        }
    }

    /// <summary>The density and window height for automatic-height mode, from the tiles' real
    /// measured <paramref name="contentHeight"/> per stage: a hand-chosen stage as it is, else the
    /// largest stage whose content plus <paramref name="chromeHeight"/> fits the work area. The
    /// height is that content plus chrome, capped to the work area (the scroll viewer takes over
    /// past it) - one concrete number the caller assigns once.</summary>
    public static (TileDensity Density, double Height) ResolveMeasuredDensityHeight(
        Func<TileDensity, double> contentHeight, double chromeHeight, TileDensity? manualOverride, double workAreaHeight, double zoom = 1)
    {
        var density = TileDensitySelector.SelectFitting(contentHeight, workAreaHeight - chromeHeight, manualOverride, zoom);
        // Rounded up to a whole pixel: the window snaps to whole pixels, and a fraction lost there
        // leaves the content a hair taller than its viewport, which shows a scroll bar for nothing.
        return (density, Math.Min(Math.Ceiling(chromeHeight + contentHeight(density) * zoom), workAreaHeight));
    }

    private static bool FullyContains(MonitorArea monitor, WindowRect rect) =>
        rect.Left >= monitor.Left && rect.Top >= monitor.Top &&
        rect.Left + rect.Width <= monitor.Right && rect.Top + rect.Height <= monitor.Bottom;

    private static bool Overlaps(MonitorArea monitor, WindowRect rect) =>
        rect.Left < monitor.Right && rect.Left + rect.Width > monitor.Left &&
        rect.Top < monitor.Bottom && rect.Top + rect.Height > monitor.Top;

    /// <summary>Moves (and, when it is larger than the area, shrinks) <paramref name="rect"/> so the
    /// whole rectangle lies inside <paramref name="monitor"/>.</summary>
    public static WindowRect ClampIntoArea(WindowRect rect, MonitorArea monitor)
    {
        var width = Math.Min(rect.Width, monitor.Width);
        var height = Math.Min(rect.Height, monitor.Height);
        var left = Math.Clamp(rect.Left, monitor.Left, monitor.Right - width);
        var top = Math.Clamp(rect.Top, monitor.Top, monitor.Bottom - height);
        return new WindowRect(left, top, width, height);
    }

    /// <summary>Pulls a shown window fully into <paramref name="area"/> without changing its size: a
    /// window centred on an owner docked at a screen edge would otherwise sit partly off-screen.
    /// Does nothing before the window has a position or when the area is unknown.</summary>
    internal static void ClampIntoArea(Window window, MonitorArea area)
    {
        if (area.Width <= 0 || area.Height <= 0 || double.IsNaN(window.Left) || double.IsNaN(window.Top))
            return;

        var placed = ClampIntoArea(new WindowRect(window.Left, window.Top, window.ActualWidth, window.ActualHeight), area);
        window.Left = placed.Left;
        window.Top = placed.Top;
    }

    /// <summary>
    /// The "move window back to the centre" recovery: centers a window of the given size on whichever
    /// monitor contains the point <paramref name="cursorX"/>/<paramref name="cursorY"/>, falling back
    /// to <paramref name="primary"/> when the point is not on any known monitor.
    /// </summary>
    public static WindowRect CenterOnCursorMonitor(
        double cursorX, double cursorY, double width, double height,
        IReadOnlyList<MonitorArea> monitors, MonitorArea primary)
    {
        var target = primary;
        foreach (var monitor in monitors)
        {
            if (cursorX >= monitor.Left && cursorX < monitor.Right && cursorY >= monitor.Top && cursorY < monitor.Bottom)
            {
                target = monitor;
                break;
            }
        }

        return CenterOn(target, width, height);
    }

    private static WindowRect CenterOn(MonitorArea monitor, double width, double height)
    {
        var clampedWidth = Math.Min(width, monitor.Width);
        var clampedHeight = Math.Min(height, monitor.Height);
        var left = monitor.Left + (monitor.Width - clampedWidth) / 2;
        var top = monitor.Top + (monitor.Height - clampedHeight) / 2;
        return new WindowRect(left, top, clampedWidth, clampedHeight);
    }
}
