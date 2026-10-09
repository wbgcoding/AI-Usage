using System.Runtime.InteropServices;
using System.Windows.Media;

namespace AiUsage.Services;

/// <summary>
/// The Windows accent color and the arithmetic that makes it usable as this app's accent: the raw color
/// is never shown as is, its lightness is moved in small steps until it reads against the surfaces it
/// is drawn on. Everything except the two registry and DWM reads is pure.
/// </summary>
internal static class AccentColors
{
    /// <summary>WCAG floor for a graphical component, the same bar every theme's accent is held to.</summary>
    public const double MinimumContrast = 3.0;

    private const double LightnessStep = 0.01;
    private const double HoverShift = 0.10;

    /// <summary>The DWM accent value (<c>0xAABBGGRR</c>) as an opaque color; the alpha byte is ignored.</summary>
    public static Color FromAbgr(uint abgr) =>
        Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));

    /// <summary>The colorization color DWM reports (<c>0xAARRGGBB</c>) as an opaque color.</summary>
    public static Color FromArgb(uint argb) =>
        Color.FromRgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    /// <summary>WCAG 2 relative luminance of an sRGB color.</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG 2 contrast ratio between two colors, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Moves the accent's HSL lightness (up for a dark surface, down for a light one) in 1 %
    /// steps until it reaches <paramref name="minimum"/> against every surface. A color that already
    /// reads is returned unchanged; one that cannot reach the ratio ends at white or black.</summary>
    public static Color EnsureContrast(Color accent, IReadOnlyCollection<Color> surfaces, bool lighten, double minimum = MinimumContrast)
    {
        double WorstContrast(Color c) => surfaces.Count == 0 ? double.MaxValue : surfaces.Min(s => Contrast(c, s));

        if (WorstContrast(accent) >= minimum)
            return accent;

        var (hue, saturation, lightness) = ToHsl(accent);
        var candidate = accent;
        while (WorstContrast(candidate) < minimum && (lighten ? lightness < 1 : lightness > 0))
        {
            lightness = Math.Clamp(lightness + (lighten ? LightnessStep : -LightnessStep), 0, 1);
            candidate = FromHsl(hue, saturation, lightness);
        }

        return candidate;
    }

    /// <summary>The hover variant: 10 % of lightness lighter on a dark theme, darker on a light one.</summary>
    public static Color Hover(Color accent, bool lighten)
    {
        var (hue, saturation, lightness) = ToHsl(accent);
        return FromHsl(hue, saturation, Math.Clamp(lightness + (lighten ? HoverShift : -HoverShift), 0, 1));
    }

    public static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        if (delta < 1e-9)
            return (0, 0, lightness);

        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (max == r)
            hue = 60 * (((g - b) / delta) % 6);
        else if (max == g)
            hue = 60 * (((b - r) / delta) + 2);
        else
            hue = 60 * (((r - g) / delta) + 4);
        return (hue < 0 ? hue + 360 : hue, saturation, lightness);
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var sector = hue / 60.0;
        var x = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = (int)Math.Floor(sector) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = lightness - chroma / 2;
        static byte ToByte(double v) => (byte)Math.Clamp(v * 255 + 0.5, 0, 255);
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

    /// <summary>The accent color Windows currently uses: the DWM accent value in the registry, and when
    /// that is missing or unreadable the colorization color DWM itself reports. Null when neither
    /// answers, which leaves the theme's own accent in place.</summary>
    public static Color? ReadSystemAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int stored)
                return FromAbgr(unchecked((uint)stored));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through to the DWM call below.
        }

        try
        {
            if (DwmGetColorizationColor(out var argb, out _) == 0)
                return FromArgb(argb);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM: keep the theme's accent.
        }

        return null;
    }
}
