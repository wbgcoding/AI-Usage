using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Scans every shipped .xaml file for Margin/Padding values, both as a plain attribute
/// (<c>Margin="0,4"</c>) and as a Style Setter (<c>&lt;Setter Property="Padding" Value="0,4"/&gt;</c>,
/// the form almost every spacing value in this project actually uses), and requires each
/// comma-separated number to sit on the spacing raster defined in <c>Themes/Tokens.xaml</c>:
/// 0, 4, 8, 12, 16 or 24.
/// </summary>
public class SpacingScaleTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage");
    }

    private static readonly int[] SpacingRaster = [0, 4, 8, 12, 16, 24];

    // Margin="-3" (the slider thumb's halo overlay) and Margin="-1" (the danger button's hover
    // overlay border) are deliberate negative visual offsets, not spacing, and are commented as
    // such at their own site. The tile name button's "-4,0,0,0" cancels its own 4 px left padding so
    // the text lines up with the bars; the tile's bar-row gap ("0,0,0,6") and the gap above its body
    // ("0,2,0,0") are the tight in-tile spacing. The figure card grid's "0,0,-8,-8" cancels the
    // gap every card keeps to its right and below, so wrapped rows stay flush with the section
    // edges. The usage bar's "0,-3" lets the pace tick stick out 3 px above and below the
    // track. They are the only values this test excuses.
    private static readonly HashSet<(string Attribute, string Value)> AllowedExceptions =
    [
        ("Margin", "-3"),
        ("Margin", "-1"),
        ("Margin", "-4,0,0,0"),
        ("Margin", "0,0,0,6"),
        ("Margin", "0,2,0,0"),
        ("Margin", "0,0,-8,-8"),
        ("Margin", "0,-3"),
    ];

    // Plain attribute form: Margin="..." or Padding="..." directly on an element.
    private static readonly Regex DirectAttribute = new(
        @"\b(Margin|Padding)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    // Style Setter form: <Setter Property="Margin" Value="..."/> (how most spacing in this
    // project is actually declared, inside the theme and window resource dictionaries).
    private static readonly Regex SetterAttribute = new(
        @"Property\s*=\s*""(Margin|Padding)""\s+Value\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    private readonly record struct Offender(string File, string Attribute, string Value);

    [Fact]
    public void EveryMarginAndPaddingValueSitsOnTheSpacingRaster()
    {
        var offenders = new List<Offender>();

        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (Match match in DirectAttribute.Matches(text))
                CheckValue(fileName, match.Groups[1].Value, match.Groups[2].Value, offenders);

            foreach (Match match in SetterAttribute.Matches(text))
                CheckValue(fileName, match.Groups[1].Value, match.Groups[2].Value, offenders);
        }

        Assert.True(offenders.Count == 0, "Spacing off the raster (0/4/8/12/16/24):\n" +
            string.Join("\n", offenders.Select(o => $"{o.File}: {o.Attribute}=\"{o.Value}\"")));
    }

    private static void CheckValue(string file, string attribute, string value, List<Offender> offenders)
    {
        if (value.StartsWith('{')) // a Binding/StaticResource/TemplateBinding markup extension
            return;
        if (AllowedExceptions.Contains((attribute, value)))
            return;

        foreach (var part in value.Split(','))
        {
            if (!double.TryParse(part, out var number) || !SpacingRaster.Contains((int)number) || number != (int)number)
            {
                offenders.Add(new Offender(file, attribute, value));
                return;
            }
        }
    }

    // A pure-measurement proof, not a live-window one (SettingsWindow cannot be safely instantiated
    // here - see AccessibilityTests' own note on the hangs that approach caused): FormattedText gives
    // the real, font-metric width of each provider's name, and the geometry constants below are the
    // literal values SettingsWindow.xaml itself declares for this row, named at their source so a
    // future edit to any of them is easy to find and update here too.
    [Fact]
    public void ProviderVisibilityButtonsFitOnOneLineAtTheDefaultSettingsWidth()
    {
        const double windowWidth = 560; // Window Width="560"
        const double windowChromeBorder = 1 * 2; // Controls.xaml "WindowChrome" style, BorderThickness="1"
        // The chrome itself carries no margin: it sits on the window edge, so nothing is subtracted for one.
        const double scrollViewerPadding = 12 * 2; // ScrollViewer Padding={StaticResource Space.M} (Tokens.xaml: 12)
        const double sidebarColumnWidth = 150; // sidebar ColumnDefinition Width="150"
        const double cardPadding = 12 * 2; // "Card" style Padding={StaticResource Space.M}
        const double cardBorder = 1 * 2; // "Card" style BorderThickness="1"

        const double contentColumnWidth = windowWidth - windowChromeBorder
            - scrollViewerPadding - sidebarColumnWidth - cardPadding - cardBorder;

        const double buttonPadding = 4 + 4; // this row's own Button Padding="4,4"
        const double iconWidth = 18; // the eye Path's Width="18"
        const double iconGap = 8; // the eye Path's Margin="0,0,8,0"
        const double buttonMarginRight = 4; // this row's own Button Margin="0,0,4,4"

        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, Arial"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // The four shipped providers' real DisplayName values (ClaudeProvider.cs, CodexProvider.cs,
        // GeminiProvider.cs, CopilotProvider.cs) - the default row before any extra account is added.
        string[] providerNames = ["Claude", "Codex", "Gemini", "Copilot"];

        var totalButtonsWidth = providerNames.Sum(name =>
            buttonPadding + iconWidth + iconGap + MeasureTextWidth(name, typeface) + buttonMarginRight);

        Assert.True(totalButtonsWidth < contentColumnWidth,
            $"Provider visibility buttons need {totalButtonsWidth:0.#}px but the content column is only " +
            $"{contentColumnWidth:0.#}px wide at the default window size.");
    }

    private static double MeasureTextWidth(string text, Typeface typeface) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 12 /* Font.Size.Body, Tokens.xaml */, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
}
