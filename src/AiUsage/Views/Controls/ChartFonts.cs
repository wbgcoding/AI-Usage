using System.Windows;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// Font lookup for the self-drawn charts, so axis labels and legends follow the active theme font
/// (monospace in the terminal theme) instead of a hard-coded family.
/// </summary>
internal static class ChartFonts
{
    /// <summary>The theme's UI font as seen from <paramref name="owner"/>, or Segoe UI when no theme
    /// resource is reachable (a control outside a window or without the app resources).</summary>
    public static FontFamily Ui(FrameworkElement owner) =>
        owner.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");

    /// <summary>The regular-weight typeface of <see cref="Ui"/>.</summary>
    public static Typeface UiTypeface(FrameworkElement owner) =>
        new(Ui(owner), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
}
