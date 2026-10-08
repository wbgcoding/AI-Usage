using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AiUsage.Tests;

/// <summary>
/// The app draws its own icons (Themes/Icons.xaml) instead of printing characters. Two things can
/// go wrong silently there: a path string that no longer parses (WPF would throw only when the
/// window is first shown), and a glyph creeping back into a view - the colour emoji this replaced
/// ignored the theme entirely and the gear character was unreadable at 16 px.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class IconTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        return Path.Combine(Path.GetDirectoryName(testsDir)!, "src", "AiUsage");
    }

    private static ResourceDictionary LoadIcons()
    {
        using var stream = File.OpenRead(Path.Combine(SrcDir, "Themes", "Icons.xaml"));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    public static IEnumerable<object[]> IconKeys() =>
        new[] { "Icon.Gear", "Icon.Eye", "Icon.EyeOff", "Icon.Layout", "Icon.Stats", "Icon.Refresh", "Icon.Minimize", "Icon.Close", "Icon.Alert" }
            .Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(IconKeys))]
    public void Every_icon_parses_and_covers_its_grid(string key)
    {
        var geometry = Assert.IsAssignableFrom<Geometry>(LoadIcons()[key]);
        var bounds = geometry.Bounds;

        Assert.False(bounds.IsEmpty, $"{key} draws nothing.");
        Assert.True(bounds.Right <= 24.01 && bounds.Bottom <= 24.01 && bounds.Left >= -0.01 && bounds.Top >= -0.01,
            $"{key} leaves the 24x24 grid: {bounds}");
        // Only the longer side, because a minimize icon is legitimately a thin dash.
        Assert.True(Math.Max(bounds.Width, bounds.Height) >= 12,
            $"{key} is only {bounds.Width:F1}x{bounds.Height:F1} on a 24x24 grid and would look lost next to the others.");
    }

    // The gear was the icon that failed hardest as a character. A plain ring would still pass the
    // bounds check above, so this asserts the teeth exist as a wavy outline rather than a disc.
    [Fact]
    public void The_gear_actually_has_teeth()
    {
        var gear = (Geometry)LoadIcons()["Icon.Gear"];
        var centre = new Point(12, 12);
        var radii = new List<double>();

        for (var angle = 0; angle < 360; angle += 3)
        {
            var direction = new Vector(Math.Cos(angle * Math.PI / 180), Math.Sin(angle * Math.PI / 180));
            var radius = 11.0;
            while (radius > 4.0 && !gear.FillContains(centre + direction * radius))
                radius -= 0.1;
            radii.Add(radius);
        }

        Assert.True(radii.Max() - radii.Min() > 2.0,
            $"The outer edge only varies by {radii.Max() - radii.Min():F1} units - that is a disc, not a cogwheel.");
    }

    [Fact]
    public void No_view_prints_a_character_where_an_icon_belongs()
    {
        var offenders =
            from file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories)
            from match in Regex.Matches(File.ReadAllText(file), @"&#\d+;").Cast<Match>()
            select $"{Path.GetFileName(file)}: {match.Value}";

        Assert.True(!offenders.Any(), "Character glyphs found where Themes/Icons.xaml should be used:\n" + string.Join("\n", offenders));
    }
}
