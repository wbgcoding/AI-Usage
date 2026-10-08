using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// "Segoe UI Variable Text" (the font Windows 11 ships for its own Fluent surfaces) confused WPF's
/// text layout into reporting a line box shorter than the glyphs it actually draws: the bottom of a
/// descender (g, j, y, q) rendered past that box and the surrounding <c>ScrollViewer</c> - present in
/// every <c>TextBox</c> - clipped it off. Swapping the font stack back to plain "Segoe UI" (still the
/// Windows system font, still the same visual family) rendered every descender in full. Confirmed on
/// the About window's archive link, which is why every theme is barred from reintroducing the
/// variable font outright rather than fixing the same clip a second time.
/// </summary>
public class ThemeFontFamilyTests
{
    [Fact]
    public void NoThemeFileNamesTheVariableSegoeFont()
    {
        var themes = Path.Combine(FindAppSourceRoot(), "Themes");
        var offenders = Directory
            .EnumerateFiles(themes, "*.xaml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Segoe UI Variable", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Segoe UI Variable clips TextBox descenders (g, j, y, q); use plain Segoe UI instead: " + string.Join(", ", offenders));
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
