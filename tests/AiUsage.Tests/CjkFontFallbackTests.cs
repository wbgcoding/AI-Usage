using System.Windows;
using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Tests;

public class CjkFontFallbackTests
{
    [Theory]
    [InlineData("Segoe UI, Arial", "ja", "Segoe UI, Yu Gothic UI, Meiryo UI, Arial")]
    [InlineData("Segoe UI, Arial", "zh-Hans", "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Arial")]
    [InlineData("Cascadia Mono, Consolas, Courier New", "ja", "Cascadia Mono, Consolas, MS Gothic, Courier New")]
    [InlineData("Cascadia Mono, Consolas, Courier New", "zh-Hans", "Cascadia Mono, Consolas, Microsoft YaHei UI, Courier New")]
    [InlineData("Segoe UI, Arial", "de", "Segoe UI, Arial")]
    [InlineData("Segoe UI, Arial", "en", "Segoe UI, Arial")]
    [InlineData("Segoe UI, Arial", "pt-BR", "Segoe UI, Arial")]
    public void Apply_inserts_the_cjk_font_before_the_last_fallback(string family, string language, string expected) =>
        Assert.Equal(expected, CjkFontFallback.Apply(family, language));

    [Fact]
    public void ApplyTo_does_not_stack_fonts_and_restores_the_theme_family()
    {
        var theme = new ResourceDictionary
        {
            ["Font.Ui"] = new FontFamily("Segoe UI, Arial"),
            ["Font.Numeric"] = new FontFamily("Segoe UI, Arial"),
        };

        CjkFontFallback.ApplyTo(theme, "ja");
        CjkFontFallback.ApplyTo(theme, "ja");
        Assert.Equal("Segoe UI, Yu Gothic UI, Meiryo UI, Arial", ((FontFamily)theme["Font.Ui"]).Source);

        CjkFontFallback.ApplyTo(theme, "zh-Hans");
        Assert.Equal("Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Arial", ((FontFamily)theme["Font.Numeric"]).Source);

        CjkFontFallback.ApplyTo(theme, "de");
        Assert.Equal("Segoe UI, Arial", ((FontFamily)theme["Font.Ui"]).Source);
    }
}

public class ThemeFontWiringTests
{
    [Fact]
    public void Theme_apply_keeps_the_fonts_for_a_non_cjk_language()
    {
        var merged = new System.Collections.ObjectModel.Collection<ResourceDictionary>();
        ThemeService.Apply(AppTheme.Dark, merged,
            _ => new ResourceDictionary { ["Font.Ui"] = new FontFamily("Segoe UI, Arial"), ["Font.Numeric"] = new FontFamily("Segoe UI, Arial") },
            () => false);

        var theme = Assert.Single(merged);
        Assert.Equal("Segoe UI, Arial", ((FontFamily)theme["Font.Ui"]).Source);
        Assert.Equal("Segoe UI, Arial", ((FontFamily)theme["Font.Numeric"]).Source);
    }
}
