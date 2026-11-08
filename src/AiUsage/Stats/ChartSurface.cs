using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>
/// What the chart colors are drawn on: the theme's surface colors, and in Windows high contrast the
/// system colors that replace the fixed palette. <see cref="ChartPalette"/> lifts each swatch to the
/// 3:1 graphics contrast (WCAG 1.4.11) against these, keeping its hue.
/// </summary>
public sealed record ChartSurface(IReadOnlyList<Color> Surfaces, IReadOnlyList<Color>? HighContrastColors = null)
{
    /// <summary>No surface known: the palette colors stay as they are.</summary>
    public static readonly ChartSurface None = new([]);

    /// <summary>True when the surfaces are dark, so a color gains contrast by getting lighter.</summary>
    public bool Lighten
    {
        get
        {
            if (Surfaces.Count == 0)
                return false;

            var black = Color.FromRgb(0, 0, 0);
            var white = Color.FromRgb(255, 255, 255);
            return Surfaces.Average(surface => AccentColors.Contrast(surface, black)) < Surfaces.Average(surface => AccentColors.Contrast(surface, white));
        }
    }

    /// <summary>The color with its lightness moved until it reads at 3:1 on every surface; hue and
    /// saturation stay. A color that already reads is returned as is.</summary>
    public Color Fit(Color color) => Surfaces.Count == 0 ? color : AccentColors.EnsureContrast(color, [.. Surfaces], Lighten);
}
