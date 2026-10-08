using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// A floating panel that carries both a drop shadow and a bitmap cache renders its whole subtree
/// into an intermediate surface, and the cache then rasterizes that surface once and stretches it
/// back on every composition pass. Text inside comes out visibly smeared while the rest of the
/// window stays crisp. It happened twice, in the drop-down list and in the context menu, so the
/// combination is now barred from the theme files outright rather than fixed a third time.
/// </summary>
public class PopupSharpnessTests
{
    [Fact]
    public void NoThemeFileRastersATextPanelIntoABitmapCache()
    {
        var themes = Path.Combine(FindAppSourceRoot(), "Themes");
        var offenders = Directory
            .EnumerateFiles(themes, "*.xaml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("CacheMode=\"BitmapCache\"", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A bitmap cache in a theme file smears the text it covers: " + string.Join(", ", offenders));
    }

    private static string FindAppSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "AiUsage")))
            dir = Path.GetDirectoryName(dir);
        return dir is null
            ? throw new InvalidOperationException("Could not locate the src/AiUsage directory from the test output path.")
            : Path.Combine(dir, "src", "AiUsage");
    }
}
