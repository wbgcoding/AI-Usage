using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AiUsage.Tests;

/// <summary>
/// WCAG 2.x contrast ratio (relative luminance of sRGB colors) for the token pairs that carry
/// readable content. Text on a surface, including the muted variant, is
/// checked against WCAG 1.4.3 (4.5:1); a level color on the track is a non-text graphical UI
/// component (a progress-bar fill), checked against the lower WCAG 1.4.11 bar (3:1) that
/// criterion actually sets for such components. Border.Control (an outline-only control's sole
/// visible identity) and Accent get that same 3:1 floor on every surface.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class ThemeContrastTests
{
    // The four literal themes only: their hex values are this app's own choice, so a computed WCAG
    // ratio genuinely tests something. HighContrast.xaml maps every token to a live SystemColors
    // brush instead (see its own file comment) - on a normal, non-high-contrast desktop those resolve
    // to whatever the ordinary desktop theme happens to use, which is not a high-contrast palette and
    // would make every ratio check here machine-dependent and meaningless. Windows' own high-contrast
    // feature is what guarantees real contrast once actually active; this file's job is only to have
    // every token this app looks up, which AllThemeFiles/HighContrast_defines_every_token_the_literal_themes_define
    // below checks instead.
    private static readonly string[] ThemeFiles = ["Nebula.xaml", "Terminal.xaml", "Dark.xaml", "Light.xaml"];

    // All five - the four literal palettes plus the SystemColors-driven high-contrast one - for the
    // completeness check below, now that AppTheme.System (still resolving to Light or Dark, never a
    // dictionary of its own) brought the total number of ways a user can end up looking at this app's
    // tokens to five.
    private static readonly string[] AllThemeFiles = ["Nebula.xaml", "Terminal.xaml", "Dark.xaml", "Light.xaml", "HighContrast.xaml"];
    private static readonly string[] TextTokens = ["Text.Primary", "Text.Secondary"];
    private static readonly string[] SurfaceTokens = ["Bg.Base", "Bg.Surface", "Bg.Raised"];
    private static readonly string[] LevelTokens = ["Level.Ok", "Level.Warn", "Level.Crit"];

    private static ResourceDictionary Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", fileName);
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static Color ColorOf(ResourceDictionary dictionary, string key) =>
        ((SolidColorBrush)dictionary[key]).Color;

    private static double RelativeLuminance(Color c)
    {
        double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var (l1, l2) = (RelativeLuminance(a), RelativeLuminance(b));
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static IEnumerable<object[]> TextOnSurfacePairs() =>
        from file in ThemeFiles
        from text in TextTokens
        from surface in SurfaceTokens
        select new object[] { file, text, surface };

    public static IEnumerable<object[]> LevelOnTrackPairs() =>
        from file in ThemeFiles
        from level in LevelTokens
        select new object[] { file, level };

    [Theory]
    [MemberData(nameof(TextOnSurfacePairs))]
    public void Text_reaches_AA_contrast_on_every_surface(string fileName, string textToken, string surfaceToken)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, textToken), ColorOf(dictionary, surfaceToken));

        Assert.True(ratio >= 4.5, $"{fileName}: {textToken} on {surfaceToken} is only {ratio:F2}:1");
    }

    [Theory]
    [MemberData(nameof(LevelOnTrackPairs))]
    public void Level_color_reaches_AA_contrast_on_the_track(string fileName, string levelToken)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, levelToken), ColorOf(dictionary, "Track"));

        Assert.True(ratio >= 3.0, $"{fileName}: {levelToken} on Track is only {ratio:F2}:1");
    }

    // The standard controls are drawn by this app now (Themes/Controls.xaml), so the token pairs
    // those templates put on top of each other need the same check the app's own surfaces get. The
    // check mark is text-sized and gets the 4.5:1 bar; the filled radio dot and the slider's filled
    // track are graphical state indicators and get the 3:1 bar of WCAG 1.4.11. Slider, scroll bar
    // and separator have no text pair at all - their shape and size are judged from a screenshot.
    public static IEnumerable<object[]> TemplatePairs() =>
        from file in ThemeFiles
        from pair in new[]
        {
            ("Bg.Base", "Accent", 4.5),   // CheckBox: the drawn check on its filled box
            ("Accent", "Bg.Raised", 3.0), // RadioButton: the filled dot inside its ring
            ("Accent", "Track", 3.0),     // Slider: the filled part of the track
        }
        select new object[] { file, pair.Item1, pair.Item2, pair.Item3 };

    [Theory]
    [MemberData(nameof(TemplatePairs))]
    public void Templated_controls_stay_readable_in_every_theme(string fileName, string front, string back, double minimum)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, front), ColorOf(dictionary, back));

        Assert.True(ratio >= minimum, $"{fileName}: {front} on {back} is only {ratio:F2}:1, needs {minimum:F1}:1");
    }

    // Without an outline (Track.BorderThickness is 0 in the four literal themes) the unfilled part of
    // a usage bar is told apart from the tile only by its own colour, so that colour needs a visible
    // step from the tile surface. 1.3:1 is the floor for a quiet shape that is not the sole carrier of
    // the value (the percentage is printed next to it). HighContrast is left out: its track is a
    // system colour and keeps its outline.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TrackStandsOutFromTheTileWithoutAnOutline(string fileName)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, "Track"), ColorOf(dictionary, "Bg.Surface"));

        Assert.True(ratio >= 1.3, $"{fileName}: Track on Bg.Surface is only {ratio:F2}:1, needs 1.3:1");
    }

    public static IEnumerable<object[]> Themes() => ThemeFiles.Select(file => new object[] { file });

    // Text.Muted lost its exemption once it started appearing as real label/value text (plan,
    // source badge, timestamps) rather than pure decoration, so it now gets the same 4.5:1 text
    // floor as every other on-surface text token. Border.Control is the stronger outline token for
    // controls whose border is their only visible identity; Accent is a regression pin (already
    // passing) now that it sits next to the other floor checks.
    public static IEnumerable<object[]> MutedControlAndAccentOnSurfacePairs() =>
        from file in ThemeFiles
        from pair in new (string Token, double Minimum)[]
        {
            ("Text.Muted", 4.5),
            ("Border.Control", 3.0),
            ("Accent", 3.0),
        }
        from surface in SurfaceTokens
        select new object[] { file, pair.Token, surface, pair.Minimum };

    [Theory]
    [MemberData(nameof(MutedControlAndAccentOnSurfacePairs))]
    public void Muted_text_control_borders_and_accent_reach_their_contrast_floor_on_every_surface(
        string fileName, string token, string surfaceToken, double minimum)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, token), ColorOf(dictionary, surfaceToken));

        Assert.True(ratio >= minimum, $"{fileName}: {token} on {surfaceToken} is only {ratio:F2}:1, needs {minimum:F1}:1");
    }

    // A surface ramp only does its job if each step is visually distinct from its neighbour - two
    // identical greys are not two surfaces. 1.05:1 is deliberately low (not the 4.5/3.0 text/graphic
    // floors above): it is a "not literally the same colour" check, chosen so the other three themes
    // (already a real ramp, 1.05-1.12:1) keep passing unchanged while catching Light's old Bg.Surface
    // == Bg.Raised == #FFFFFF (1.00:1).
    public static IEnumerable<object[]> SurfaceRampPairs() =>
        from file in ThemeFiles
        from pair in new[] { ("Bg.Base", "Bg.Surface"), ("Bg.Surface", "Bg.Raised") }
        select new object[] { file, pair.Item1, pair.Item2 };

    [Theory]
    [MemberData(nameof(SurfaceRampPairs))]
    public void Adjacent_surfaces_in_the_ramp_are_visually_distinct(string fileName, string lower, string higher)
    {
        var dictionary = Load(fileName);
        var ratio = ContrastRatio(ColorOf(dictionary, lower), ColorOf(dictionary, higher));

        Assert.True(ratio >= 1.05, $"{fileName}: {lower} vs {higher} is only {ratio:F3}:1, needs 1.05:1");
    }

    // Every token any theme dictionary is looked up by (App.xaml's own Nebula default names the
    // full set) must resolve in every one of the five files, HighContrast.xaml included - a token
    // that only three of the four literal themes carry has already shipped a silent DynamicResource
    // miss once (see the class doc on Level.*.Fill), and the same gap in the SystemColors-driven
    // dictionary would only ever surface as a blank swatch, never a build or ratio failure.
    [Fact]
    public void Every_theme_file_defines_the_same_set_of_tokens()
    {
        var expectedKeys = Load("Nebula.xaml").Keys.Cast<string>().ToHashSet();

        foreach (var file in AllThemeFiles)
        {
            var actualKeys = Load(file).Keys.Cast<string>().ToHashSet();
            var missing = expectedKeys.Except(actualKeys).ToList();
            Assert.True(missing.Count == 0, $"{file} is missing token(s): {string.Join(", ", missing)}");
        }
    }
}
