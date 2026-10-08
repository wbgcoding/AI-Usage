using System.Windows.Media;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

public class ChartPaletteTests
{
    // Ten plausible-looking model provider ids this palette knows nothing about - picked once and
    // verified (by running the code) to satisfy the acceptance criterion below: every one of their
    // hues lands at least 25° away from all five provider hues, and no two of the ten share a hue.
    private static readonly string[] UnknownNames =
        ["glm", "minimax", "kimi", "deepseek", "qwen", "mistral", "grok", "llama", "phi", "yi"];

    [Theory]
    [InlineData("claude", 0xD9, 0x77, 0x57)]
    [InlineData("codex", 0x10, 0xA3, 0x7F)]
    [InlineData("cursor", 0x6E, 0x56, 0xCF)]
    [InlineData("gemini", 0x42, 0x85, 0xF4)]
    [InlineData("copilot", 0x89, 0x57, 0xE5)]
    public void ForProvider_returns_the_five_fixed_colors_for_the_known_ids(string providerId, byte r, byte g, byte b) =>
        Assert.Equal(Color.FromRgb(r, g, b), ChartPalette.ForProvider(providerId));

    [Fact]
    public void ForProvider_returns_the_same_color_for_the_same_unknown_name_every_time() =>
        Assert.Equal(ChartPalette.ForProvider("glm"), ChartPalette.ForProvider("glm"));

    [Fact]
    public void ForProvider_spreads_ten_unknown_names_across_ten_distinct_hues_far_from_every_provider()
    {
        var providerHues = new[] { "claude", "codex", "cursor", "gemini", "copilot" }
            .Select(id => Hue(ChartPalette.ForProvider(id)))
            .ToList();
        var unknownHues = UnknownNames.Select(name => Hue(ChartPalette.ForProvider(name))).ToList();

        Assert.Equal(10, unknownHues.Distinct().Count());
        Assert.All(unknownHues, hue => Assert.All(providerHues, providerHue => Assert.True(
            HueDistance(hue, providerHue) >= 25.0,
            $"hue {hue:F1} is only {HueDistance(hue, providerHue):F1} degrees from provider hue {providerHue:F1}")));
    }

    [Fact]
    public void ForModel_lightens_every_further_model_while_keeping_the_providers_own_hue()
    {
        var largest = ChartPalette.ForModel("claude", 0);
        var second = ChartPalette.ForModel("claude", 1);

        Assert.NotEqual(largest, second);
        // Within a degree, not exact: HslToRgb rounds to whole bytes, so reading the hue back out of
        // the lightened color's own byte-quantized RGB drifts a little from the hue that produced it.
        Assert.True(HueDistance(Hue(largest), Hue(second)) < 1.0);
    }

    private static double Hue(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max == min)
            return 0;

        var d = max - min;
        var h = max == r
            ? (g - b) / d + (g < b ? 6 : 0)
            : max == g
                ? (b - r) / d + 2
                : (r - g) / d + 4;
        return h * 60.0;
    }

    private static double HueDistance(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }
}
