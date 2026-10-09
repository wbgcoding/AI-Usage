using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Tests;

public class ThemeAccentTests
{
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DarkSurface = Color.FromRgb(0x20, 0x20, 0x23);

    private static ResourceDictionary Fixture(Uri uri)
    {
        var light = uri.OriginalString.Contains("Light", StringComparison.Ordinal);
        return new ResourceDictionary
        {
            ["Bg.Base"] = new SolidColorBrush(light ? Color.FromRgb(0xF1, 0xF2, 0xF6) : Color.FromRgb(0x18, 0x18, 0x1B)),
            ["Bg.Surface"] = new SolidColorBrush(light ? White : DarkSurface),
            ["Bg.Raised"] = new SolidColorBrush(light ? Color.FromRgb(0xF6, 0xF7, 0xFA) : Color.FromRgb(0x28, 0x28, 0x2C)),
            ["Accent"] = new SolidColorBrush(Color.FromRgb(1, 2, 3)),
            ["Accent.Hover"] = new SolidColorBrush(Color.FromRgb(4, 5, 6)),
        };
    }

    private static ResourceDictionary ApplyOnce(AppTheme theme, bool windowsLight, Color? windowsAccent)
    {
        var merged = new Collection<ResourceDictionary>();
        ThemeService.Apply(theme, merged, Fixture, () => false, () => windowsLight, () => windowsAccent);
        return Assert.Single(merged);
    }

    private static Color AccentOf(ResourceDictionary d) => ((SolidColorBrush)d["Accent"]).Color;
    private static Color HoverOf(ResourceDictionary d) => ((SolidColorBrush)d["Accent.Hover"]).Color;

    [Fact]
    public void Abgr_registry_value_becomes_the_matching_rgb_color()
    {
        // 0xFF (alpha) 0x10 (blue) 0x20 (green) 0x30 (red)
        Assert.Equal(Color.FromRgb(0x30, 0x20, 0x10), AccentColors.FromAbgr(0xFF102030));
    }

    [Fact]
    public void Dwm_argb_value_becomes_the_matching_rgb_color()
    {
        Assert.Equal(Color.FromRgb(0x30, 0x20, 0x10), AccentColors.FromArgb(0xC4302010));
    }

    [Fact]
    public void Pure_yellow_on_a_light_surface_is_darkened_to_at_least_three_to_one()
    {
        var yellow = Color.FromRgb(0xFF, 0xFF, 0x00);

        var corrected = AccentColors.EnsureContrast(yellow, [White], lighten: false);

        Assert.True(AccentColors.Contrast(corrected, White) >= 3.0);
        Assert.NotEqual(yellow, corrected);
        var (hue, _, _) = AccentColors.ToHsl(corrected);
        Assert.InRange(hue, 55, 65);
    }

    [Fact]
    public void A_mid_blue_that_already_reads_is_left_unchanged()
    {
        var blue = Color.FromRgb(0x25, 0x63, 0xEB);

        Assert.Equal(blue, AccentColors.EnsureContrast(blue, [White], lighten: false));
    }

    [Fact]
    public void A_dark_navy_on_a_dark_surface_is_lightened_to_at_least_three_to_one()
    {
        var navy = Color.FromRgb(0x00, 0x00, 0x80);

        var corrected = AccentColors.EnsureContrast(navy, [DarkSurface], lighten: true);

        Assert.True(AccentColors.Contrast(corrected, DarkSurface) >= 3.0);
    }

    [Fact]
    public void Hover_is_ten_percent_lighter_on_dark_and_darker_on_light()
    {
        var accent = Color.FromRgb(0x25, 0x63, 0xEB);
        var baseLightness = AccentColors.ToHsl(accent).Lightness;

        Assert.Equal(baseLightness + 0.10, AccentColors.ToHsl(AccentColors.Hover(accent, lighten: true)).Lightness, 2);
        Assert.Equal(baseLightness - 0.10, AccentColors.ToHsl(AccentColors.Hover(accent, lighten: false)).Lightness, 2);
    }

    [Fact]
    public void System_theme_with_a_red_accent_gives_a_reddish_accent_in_light_mode()
    {
        var dictionary = ApplyOnce(AppTheme.System, windowsLight: true, Color.FromRgb(0xE8, 0x11, 0x23));

        var accent = AccentOf(dictionary);
        Assert.True(accent.R > accent.G + 60 && accent.R > accent.B + 60);
        Assert.NotEqual(AccentOf(dictionary), HoverOf(dictionary));
    }

    [Fact]
    public void System_theme_with_a_red_accent_gives_a_reddish_accent_in_dark_mode()
    {
        var dictionary = ApplyOnce(AppTheme.System, windowsLight: false, Color.FromRgb(0xE8, 0x11, 0x23));

        var accent = AccentOf(dictionary);
        Assert.True(accent.R > accent.G + 60 && accent.R > accent.B + 60);
        Assert.True(AccentColors.Contrast(accent, DarkSurface) >= 3.0);
    }

    [Fact]
    public void System_theme_never_shows_a_raw_color_that_fails_contrast()
    {
        var dictionary = ApplyOnce(AppTheme.System, windowsLight: true, Color.FromRgb(0xFF, 0xFF, 0x00));

        Assert.True(AccentColors.Contrast(AccentOf(dictionary), White) >= 3.0);
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Nebula)]
    [InlineData(AppTheme.Terminal)]
    public void Other_themes_keep_their_own_accent(AppTheme theme)
    {
        var dictionary = ApplyOnce(theme, windowsLight: true, Color.FromRgb(0xE8, 0x11, 0x23));

        Assert.Equal(Color.FromRgb(1, 2, 3), AccentOf(dictionary));
        Assert.Equal(Color.FromRgb(4, 5, 6), HoverOf(dictionary));
    }

    [Fact]
    public void System_theme_without_a_windows_accent_keeps_the_theme_accent()
    {
        var dictionary = ApplyOnce(AppTheme.System, windowsLight: true, windowsAccent: null);

        Assert.Equal(Color.FromRgb(1, 2, 3), AccentOf(dictionary));
    }

    [Fact]
    public void High_contrast_keeps_the_system_colors_even_in_theme_System()
    {
        var merged = new Collection<ResourceDictionary>();
        ThemeService.Apply(AppTheme.System, merged, Fixture, () => true, () => true, () => Color.FromRgb(0xE8, 0x11, 0x23));

        Assert.Equal(Color.FromRgb(1, 2, 3), AccentOf(Assert.Single(merged)));
    }
}

public class AppearanceHookTests
{
    [Theory]
    [InlineData(0x001A, "ImmersiveColorSet", true)]
    [InlineData(0x001A, "intl", false)]
    [InlineData(0x001A, null, false)]
    [InlineData(0x0320, null, true)]
    [InlineData(0x0005, "ImmersiveColorSet", false)]
    public void Only_color_notices_trigger_a_reapply(int msg, string? setting, bool expected) =>
        Assert.Equal(expected, AppearanceHook.IsColorNotice(msg, setting));
}
