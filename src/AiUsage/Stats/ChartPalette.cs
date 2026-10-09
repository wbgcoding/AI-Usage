using System.Text;
using System.Windows.Media;

namespace AiUsage.Stats;

/// <summary>
/// Recognition colors for the statistics charts: every provider keeps one fixed hue everywhere it
/// appears, and every model inherits its own provider's hue, told apart from its siblings by
/// lightness alone. Pure - nothing here depends on WPF beyond <see cref="Color"/> itself, so every
/// swatch is unit testable without a visual tree.
/// </summary>
public static class ChartPalette
{
    private static readonly Dictionary<string, Color> ProviderColors = new()
    {
        ["claude"] = Color.FromRgb(0xD9, 0x77, 0x57),
        ["codex"] = Color.FromRgb(0x10, 0xA3, 0x7F),
        ["cursor"] = Color.FromRgb(0x6E, 0x56, 0xCF),
        ["gemini"] = Color.FromRgb(0x42, 0x85, 0xF4),
        ["copilot"] = Color.FromRgb(0x89, 0x57, 0xE5),
    };

    /// <summary>The colors of a chart's own categories (the models of a stacked column, say), in the
    /// order they are handed out: spread around the color wheel so neighbours stay apart.</summary>
    private static readonly Color[] CategoricalColors =
    [
        Color.FromRgb(0x4E, 0x79, 0xA7),
        Color.FromRgb(0xF2, 0x8E, 0x2B),
        Color.FromRgb(0x59, 0xA1, 0x4F),
        Color.FromRgb(0xE1, 0x57, 0x59),
        Color.FromRgb(0xB0, 0x7A, 0xA1),
        Color.FromRgb(0x76, 0xB7, 0xB2),
        Color.FromRgb(0xED, 0xC9, 0x48),
        Color.FromRgb(0x9C, 0x75, 0x5F),
    ];

    /// <summary>The color of the <paramref name="index"/>-th category of a chart, in a fixed order;
    /// past the last color the sequence starts over.</summary>
    public static Color Categorical(int index) => CategoricalColors[((index % CategoricalColors.Length) + CategoricalColors.Length) % CategoricalColors.Length];

    private static readonly double[] ProviderHues = [.. ProviderColors.Values.Select(color => ToHsl(color).H)];

    /// <summary>Every fixed provider hue, for <see cref="ProjectColorResolver"/> to keep its own
    /// stable-hash fallback away from - a project's color should never coincide with a provider's,
    /// the same reasoning <see cref="BuildUnknownHueSlots"/> already applies to an unrecognised
    /// provider or model.</summary>
    internal static IReadOnlyList<double> ProviderHueList => ProviderHues;

    /// <summary>The color that names a provider everywhere its share of tokens is drawn - one of the
    /// five fixed brand-adjacent colors for a known provider id, or a deterministic swatch off the
    /// golden-angle sequence below for anything else (an empty id included).</summary>
    public static Color ForProvider(string providerId)
    {
        if (!string.IsNullOrEmpty(providerId) && ProviderColors.TryGetValue(providerId, out var color))
            return color;

        return FromHsl(UnknownHue(providerId ?? ""), 0.55, 0.55);
    }

    /// <summary>The color that names one model within <paramref name="providerId"/>'s own share: the
    /// provider's exact color for the largest model (<paramref name="rankWithinProvider"/> 0), each
    /// further model 12% lighter in HSL space than the one before it, saturation untouched and
    /// lightness never pushed past 80%. A provider id this palette does not recognize falls back to
    /// <see cref="ForProvider"/> outright - there is no per-provider hue to ramp lightness against,
    /// so every model under it shares that one deterministic swatch instead.</summary>
    public static Color ForModel(string providerId, int rankWithinProvider)
    {
        if (string.IsNullOrEmpty(providerId) || !ProviderColors.TryGetValue(providerId, out var baseColor))
            return ForProvider(providerId);

        if (rankWithinProvider <= 0)
            return baseColor;

        var hsl = ToHsl(baseColor);
        var lightness = Math.Min(0.8, hsl.L + 0.12 * rankWithinProvider);
        return FromHsl(hsl.H, hsl.S, lightness);
    }

    // A provider-less swatch: a fixed hue sequence stepped by the golden angle (never repeats, and
    // spreads new hues evenly around the circle regardless of how many are drawn before it), starting
    // at 0° and skipping every candidate within 25° of one of the five provider hues above. Which
    // survivor a given name lands on is picked by a stable checksum over that name, not by insertion
    // order - so this stays a pure function of the name alone, and the same unknown model or provider
    // always gets the same color back, in any order and across any number of calls.
    private const double GoldenAngle = 137.508;
    private const double MinHueDistanceFromProviders = 25.0;
    private const int CandidateSteps = 360;

    private static readonly double[] UnknownHueSlots = BuildUnknownHueSlots();

    private static double[] BuildUnknownHueSlots()
    {
        var slots = new List<double>();
        for (var step = 0; step < CandidateSteps; step++)
        {
            var hue = step * GoldenAngle % 360.0;
            if (ProviderHues.All(providerHue => HueDistance(hue, providerHue) >= MinHueDistanceFromProviders))
                slots.Add(hue);
        }
        return [.. slots];
    }

    private static double UnknownHue(string name)
    {
        var seed = Fnv1a(name);
        return UnknownHueSlots[(int)(seed % (uint)UnknownHueSlots.Length)];
    }

    /// <summary>The general form <see cref="BuildUnknownHueSlots"/> is one fixed call of - <see
    /// cref="ProjectColorResolver"/> reuses it with a growing exclusion set (every provider color
    /// plus every project already resolved earlier in the same batch), so two projects in one list
    /// never land on the same hue purely by hash coincidence, the same way no unknown provider or
    /// model ever lands within <see cref="MinHueDistanceFromProviders"/> of a real provider's own
    /// hue.</summary>
    internal static IReadOnlyList<double> HueSlotsAvoiding(IEnumerable<double> excludedHues, double minHueDistance)
    {
        var excluded = excludedHues as IReadOnlyCollection<double> ?? excludedHues.ToList();
        var slots = new List<double>();
        for (var step = 0; step < CandidateSteps; step++)
        {
            var hue = step * GoldenAngle % 360.0;
            if (excluded.All(other => HueDistance(hue, other) >= minHueDistance))
                slots.Add(hue);
        }
        // An exclusion set dense enough to leave no slot at all (many projects, or a very wide
        // minimum distance) falls back to the plain golden-angle circle with no exclusion, rather
        // than an empty list a caller would have nothing to index into.
        return slots.Count > 0 ? slots : [.. Enumerable.Range(0, CandidateSteps).Select(step => step * GoldenAngle % 360.0)];
    }

    /// <summary>Picks one of <paramref name="slots"/> by a stable checksum over <paramref
    /// name="name"/> - the same deterministic-by-name selection <see cref="UnknownHue"/> already
    /// uses, generalised to a caller-supplied slot list.</summary>
    internal static double PickStableHue(string name, IReadOnlyList<double> slots) =>
        slots[(int)(Fnv1a(name) % (uint)slots.Count)];

    /// <summary>Hue (0..360), saturation and lightness (both 0..1) of an arbitrary color - <see
    /// cref="ProjectColorResolver"/>'s own reading of an icon's pixels, a name's color word, or a
    /// cached swatch it needs the hue back out of, before <see cref="NormalizeToPaletteBand"/> puts
    /// the hue it settles on into the same saturation/lightness band every other chart color already
    /// uses.</summary>
    internal static (double H, double S, double L) HslOf(Color color)
    {
        var hsl = ToHsl(color);
        return (hsl.H, hsl.S, hsl.L);
    }

    /// <summary>Rebuilds a color from just its hue, at the exact saturation and lightness <see
    /// cref="ForProvider"/> already uses for an unrecognized id - the one fixed band every
    /// project color (whatever it was derived from: an icon, a color word, or a stable hash) is
    /// normalised into, so a project never reads brighter, darker or more saturated than the
    /// providers and models drawn right next to it.</summary>
    internal static Color NormalizeToPaletteBand(double hue) => FromHsl(hue, 0.55, 0.55);

    /// <summary>FNV-1a over the UTF-8 bytes - a stable checksum across runs and machines, unlike
    /// <see cref="string.GetHashCode()"/>, which .NET deliberately randomizes per process.</summary>
    private static uint Fnv1a(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    private static double HueDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    private readonly record struct Hsl(double H, double S, double L);

    private static Hsl ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (max == min)
            return new Hsl(0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        var h = max == r
            ? (g - b) / d + (g < b ? 6 : 0)
            : max == g
                ? (b - r) / d + 2
                : (r - g) / d + 4;
        return new Hsl(h * 60.0, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 0)
        {
            var gray = ToByteChannel(l);
            return Color.FromRgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
        var g = HueToRgb(p, q, h / 360.0);
        var b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
        return Color.FromRgb(ToByteChannel(r), ToByteChannel(g), ToByteChannel(b));
    }

    /// <summary>A 0..1 color channel rounded to its nearest byte - round-half-up, not the framework's
    /// own banker's-rounding default, though the difference never matters here since an exact half
    /// (a channel of precisely 0.5, 1.5, ... 254.5 out of 255) essentially never occurs from an HSL
    /// conversion.</summary>
    private static byte ToByteChannel(double channel) => (byte)Math.Clamp(channel * 255.0 + 0.5, 0, 255);

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
            t += 1;
        if (t > 1)
            t -= 1;
        if (t < 1.0 / 6.0)
            return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0)
            return q;
        if (t < 2.0 / 3.0)
            return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
