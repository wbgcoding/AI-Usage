using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AiUsage.Tests;

/// <summary>
/// The usage bar's fill (Views/Controls/UsageBar.xaml) reads a gradient token per level instead of
/// the flat Level.* color: same color at the left, 15% lighter at the right. The lighter stop is
/// never pasted as a literal here - it is computed from the same formula the theme files themselves
/// were authored from, so a future change to a flat Level.* color that forgets to update its
/// gradient counterpart fails this test instead of silently drifting.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class ThemeGradientTests
{
    private static readonly string[] ThemeFiles = ["Nebula.xaml", "Terminal.xaml", "Dark.xaml", "Light.xaml"];
    private static readonly string[] LevelTokens = ["Level.Ok", "Level.Warn", "Level.Crit"];

    private static ResourceDictionary Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", fileName);
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static Color FlatColorOf(ResourceDictionary dictionary, string levelToken) =>
        ((SolidColorBrush)dictionary[levelToken]).Color;

    // 15% of each channel's own distance to 255, rounded away from zero to match how the theme
    // files were hand-authored (several channels land exactly on a .5 boundary).
    private static Color Lighten15Percent(Color color)
    {
        byte Lighten(byte channel) => (byte)Math.Round(channel + 0.15 * (255 - channel), MidpointRounding.AwayFromZero);
        return Color.FromRgb(Lighten(color.R), Lighten(color.G), Lighten(color.B));
    }

    public static IEnumerable<object[]> ThemesAndLevels() =>
        from file in ThemeFiles
        from level in LevelTokens
        select new object[] { file, level };

    [Theory]
    [MemberData(nameof(ThemesAndLevels))]
    public void Gradient_fill_starts_at_the_flat_level_color_and_ends_15_percent_lighter(string fileName, string levelToken)
    {
        var dictionary = Load(fileName);
        var flatColor = FlatColorOf(dictionary, levelToken);
        var expectedLighter = Lighten15Percent(flatColor);

        var gradient = Assert.IsType<LinearGradientBrush>(dictionary[$"{levelToken}.Fill"]);
        Assert.Equal(2, gradient.GradientStops.Count);

        var start = gradient.GradientStops.Single(s => s.Offset == 0);
        var end = gradient.GradientStops.Single(s => s.Offset == 1);

        Assert.Equal(flatColor, start.Color);
        Assert.Equal(expectedLighter, end.Color);
        Assert.Equal(new Point(0, 0.5), gradient.StartPoint);
        Assert.Equal(new Point(1, 0.5), gradient.EndPoint);
    }

    [Theory]
    [MemberData(nameof(LevelTokensData))]
    public void High_contrast_carries_a_solid_brush_instead_of_a_gradient(string levelToken)
    {
        var dictionary = Load("HighContrast.xaml");

        var brush = Assert.IsType<SolidColorBrush>(dictionary[$"{levelToken}.Fill"]);
        var flatBrush = Assert.IsType<SolidColorBrush>(dictionary[levelToken]);
        Assert.Equal(flatBrush.Color, brush.Color);
    }

    public static IEnumerable<object[]> LevelTokensData() => LevelTokens.Select(l => new object[] { l });
}
