using System.Windows;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// Colours and sizes of the self-drawn charts from the active theme. A chart's brush properties have
/// no default of their own: a brush the page did not set resolves here, at render time, from the
/// theme resource of its role, so a chart outside the stats window still follows Light, Dark and
/// HighContrast instead of a fixed named colour. The system colours are the last fallback for a
/// control without the app resources.
/// </summary>
internal static class ChartTheme
{
    private static Brush Resolve(FrameworkElement owner, Brush? set, string key, Brush fallback) =>
        set ?? owner.TryFindResource(key) as Brush ?? fallback;

    /// <summary>Axis figures, labels and legend text.</summary>
    public static Brush Text(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Text.Secondary", SystemColors.ControlTextBrush);

    /// <summary>Quieter text: empty-state lines, axes, a figure beside a label.</summary>
    public static Brush Muted(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Text.Muted", SystemColors.GrayTextBrush);

    /// <summary>The muted text colour of the system, for a brush that is not a plain colour.</summary>
    public static Color MutedColor => SystemColors.GrayTextColor;

    /// <summary>Accent marks: focus rings, overlay lines, the single-colour bars.</summary>
    public static Brush Accent(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Accent", SystemColors.HighlightBrush);

    /// <summary>Empty cells and the bar of a series that has no colour of its own.</summary>
    public static Brush Track(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Track", SystemColors.ControlDarkBrush);

    /// <summary>The ToolTip style's own popup background.</summary>
    public static Brush TooltipBackground(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Bg.Popup", SystemColors.WindowBrush);

    /// <summary>The ToolTip style's own border.</summary>
    public static Brush TooltipBorder(FrameworkElement owner, Brush? set) =>
        Resolve(owner, set, "Border", SystemColors.ActiveBorderBrush);

    /// <summary>A <c>Font.Size.*</c> token of the theme, <paramref name="fallback"/> when it is not reachable.</summary>
    public static double FontSize(FrameworkElement owner, string key, double fallback) =>
        owner.TryFindResource(key) is double size ? size : fallback;
}
