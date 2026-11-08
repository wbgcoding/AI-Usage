using System.Windows;
using System.Windows.Media;
using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

public class ChartPaletteSurfaceTests
{
    private static ChartSurface Surface(params string[] hex) =>
        new([.. hex.Select(h => (Color)ColorConverter.ConvertFromString(h))]);

    private static readonly ChartSurface Light = Surface("#F1F2F6", "#FFFFFF", "#F6F7FA");
    private static readonly ChartSurface Nebula = Surface("#1D1233", "#291A47", "#352259");
    private static readonly ChartSurface Dark = Surface("#18181B", "#202023", "#28282C");
    private static readonly ChartSurface Terminal = Surface("#0A0E0A", "#0F150F", "#161C16");

    public static IEnumerable<object[]> Surfaces() =>
        [[nameof(Light), Light], [nameof(Nebula), Nebula], [nameof(Dark), Dark], [nameof(Terminal), Terminal]];

    private static IEnumerable<Color> AllSwatches(ChartSurface surface)
    {
        foreach (var id in new[] { "claude", "codex", "cursor", "gemini", "copilot", "glm", "xhigh" })
        {
            yield return ChartPalette.ForProvider(id, surface);
            for (var rank = 0; rank < 6; rank++)
                yield return ChartPalette.ForModel(id, rank, surface);
        }

        for (var index = 0; index < 8; index++)
            yield return ChartPalette.Categorical(index, surface);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void EverySwatchReadsAtThreeToOneOnEverySurface(string name, ChartSurface surface)
    {
        foreach (var color in AllSwatches(surface))
            foreach (var background in surface.Surfaces)
                Assert.True(AccentColors.Contrast(color, background) >= AccentColors.MinimumContrast - 0.02,
                    $"{name}: {color} on {background} = {AccentColors.Contrast(color, background):0.00}");
    }

    [Fact]
    public void LightSurfaceDarkensTheYellowAndKeepsItsHue()
    {
        var yellow = ChartPalette.Categorical(6, Light);

        Assert.NotEqual(Color.FromRgb(0xED, 0xC9, 0x48), yellow);
        Assert.InRange(AccentColors.ToHsl(yellow).Hue, 40, 55);
    }

    [Fact]
    public void ColorThatAlreadyReadsStaysUntouched()
    {
        Assert.Equal(Color.FromRgb(0x42, 0x85, 0xF4), ChartPalette.ForProvider("gemini", Light));
        Assert.Equal(Color.FromRgb(0xD9, 0x77, 0x57), ChartPalette.ForProvider("claude", ChartSurface.None));
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void ModelRanksStayApartOnEverySurface(string name, ChartSurface surface)
    {
        var colors = Enumerable.Range(0, 4).Select(rank => ChartPalette.ForModel("claude", rank, surface)).ToList();

        Assert.True(colors.Distinct().Count() >= 3, $"{name}: {string.Join(", ", colors)}");
    }

    [Fact]
    public void HighContrastUsesTheSystemColorsOnly()
    {
        var system = new[] { SystemColors.WindowTextColor, SystemColors.HotTrackColor, SystemColors.HighlightColor, SystemColors.GrayTextColor };
        var hc = new ChartSurface([SystemColors.WindowColor], system);

        foreach (var color in AllSwatches(hc))
            Assert.Contains(color, system);
        Assert.NotEqual(ChartPalette.ForProvider("claude", hc), ChartPalette.ForProvider("codex", hc));
    }

    [Fact]
    public void ThemeDictionaryYieldsItsSurfacesAndTheHighContrastSet()
    {
        var theme = new ResourceDictionary
        {
            ["Bg.Base"] = new SolidColorBrush(Color.FromRgb(1, 2, 3)),
            ["Bg.Surface"] = new SolidColorBrush(Color.FromRgb(4, 5, 6)),
            ["Bg.Raised"] = new SolidColorBrush(Color.FromRgb(7, 8, 9)),
        };

        Assert.Equal(3, ThemeService.ChartSurfaceFor(theme, false).Surfaces.Count);
        Assert.Null(ThemeService.ChartSurfaceFor(theme, false).HighContrastColors);
        Assert.Equal(4, ThemeService.ChartSurfaceFor(theme, true).HighContrastColors!.Count);
    }
}
