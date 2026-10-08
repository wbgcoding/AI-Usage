using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ThemeTokenTests
{
    private static readonly string[] ThemeFiles = ["Nebula.xaml", "Terminal.xaml", "Dark.xaml", "Light.xaml"];

    private static readonly Dictionary<string, Type> ExpectedTokens = new()
    {
        ["Bg.Base"] = typeof(SolidColorBrush),
        ["Bg.Surface"] = typeof(SolidColorBrush),
        ["Bg.Raised"] = typeof(SolidColorBrush),
        ["Border"] = typeof(SolidColorBrush),
        ["Border.Window"] = typeof(SolidColorBrush),
        ["Border.Control"] = typeof(SolidColorBrush),
        ["Track"] = typeof(SolidColorBrush),
        ["Track.BorderThickness"] = typeof(Thickness),
        ["Text.Primary"] = typeof(SolidColorBrush),
        ["Text.Secondary"] = typeof(SolidColorBrush),
        ["Text.Muted"] = typeof(SolidColorBrush),
        ["Accent"] = typeof(SolidColorBrush),
        ["Accent.Hover"] = typeof(SolidColorBrush),
        ["Level.Ok"] = typeof(SolidColorBrush),
        ["Level.Warn"] = typeof(SolidColorBrush),
        ["Level.Crit"] = typeof(SolidColorBrush),
        ["Shadow"] = typeof(Color),
        ["Font.Ui"] = typeof(FontFamily),
        ["Font.Numeric"] = typeof(FontFamily),
    };

    // Checked separately from ExpectedTokens above: a literal theme carries these as a gradient
    // (Views/Controls/UsageBar.xaml's fill), while HighContrast.xaml carries plain solid brushes
    // for the same keys (ThemeGradientTests documents why) - one shared expected-type map could
    // not describe both.
    private static readonly string[] GradientTokenKeys = ["Level.Ok.Fill", "Level.Warn.Fill", "Level.Crit.Fill"];

    private static ResourceDictionary Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", fileName);
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    public static IEnumerable<object[]> Themes() => ThemeFiles.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_theme_defines_every_token_with_the_expected_type(string fileName)
    {
        var dictionary = Load(fileName);

        foreach (var (key, expectedType) in ExpectedTokens)
        {
            Assert.True(dictionary.Contains(key), $"{fileName} is missing token '{key}'");
            Assert.IsType(expectedType, dictionary[key]);
        }
        foreach (var key in GradientTokenKeys)
        {
            Assert.True(dictionary.Contains(key), $"{fileName} is missing gradient token '{key}'");
            Assert.IsType<LinearGradientBrush>(dictionary[key]);
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_theme_defines_no_more_and_no_less_than_the_twenty_four_tokens(string fileName)
    {
        var dictionary = Load(fileName);

        Assert.Equal(ExpectedTokens.Count + GradientTokenKeys.Length, dictionary.Count);
    }

    [Fact]
    public void The_high_contrast_dictionary_also_defines_every_token_with_the_expected_type()
    {
        var dictionary = Load("HighContrast.xaml");

        foreach (var (key, expectedType) in ExpectedTokens)
        {
            Assert.True(dictionary.Contains(key), $"HighContrast.xaml is missing token '{key}'");
            Assert.IsType(expectedType, dictionary[key]);
        }
        foreach (var key in GradientTokenKeys)
        {
            Assert.True(dictionary.Contains(key), $"HighContrast.xaml is missing gradient token '{key}'");
            Assert.IsType<SolidColorBrush>(dictionary[key]);
        }
    }

    // The bar track has no outline in the four literal themes (it stands out through its own colour);
    // high contrast keeps a one pixel outline because its track colour is a system colour.
    [Theory]
    [InlineData("Nebula.xaml", 0)]
    [InlineData("Terminal.xaml", 0)]
    [InlineData("Dark.xaml", 0)]
    [InlineData("Light.xaml", 0)]
    [InlineData("HighContrast.xaml", 1)]
    public void The_bar_track_outline_is_off_except_in_high_contrast(string fileName, double expected)
    {
        var thickness = Assert.IsType<Thickness>(Load(fileName)["Track.BorderThickness"]);

        Assert.Equal(new Thickness(expected), thickness);
    }

    [Fact]
    public void The_icon_dictionary_defines_the_warning_icon_as_a_geometry()
    {
        var dictionary = Load("Icons.xaml");

        Assert.NotNull(dictionary["Icon.Warning"]);
        Assert.IsAssignableFrom<Geometry>(dictionary["Icon.Warning"]);
    }
}
