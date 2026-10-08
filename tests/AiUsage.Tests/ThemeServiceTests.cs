using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Tests;

public class ThemeServiceTests
{
    // Stands in for the real pack-URI load, which needs a live Application unavailable here -
    // this seam exercises only the "find and replace the marked entry" logic that is actually ours.
    private static ResourceDictionary FakeLoad(Uri uri) => new();

    // Same, but with a "Bg.Base" entry present - what every real theme dictionary carries, and what
    // Apply's own window-background brush is built from.
    private static readonly Color FakeBaseColor = Color.FromRgb(0x11, 0x22, 0x33);
    private static ResourceDictionary FakeLoadWithBaseColor(Uri uri) => new() { ["Bg.Base"] = new SolidColorBrush(FakeBaseColor) };

    private static bool NotHighContrast() => false;
    private static bool IsHighContrast() => true;

    private const string MarkerKey = "AiUsage.ThemeService.ActiveTheme";

    // ResourceDictionary's own Source setter tries to actually resolve and load the pack URI, which
    // needs a live Application this test project deliberately has none of (same reason FakeLoad
    // exists) - writing the backing field directly gives a dictionary that reports the right Source
    // without any of that, which is all App.xaml's startup-merged default needs to be stood in for.
    private static ResourceDictionary FakeSourcedDictionary(Uri uri)
    {
        var dictionary = new ResourceDictionary();
        var field = typeof(ResourceDictionary).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(dictionary, uri);
        return dictionary;
    }

    [Fact]
    public void Apply_adds_the_requested_theme_when_none_is_present_yet()
    {
        var merged = new Collection<ResourceDictionary>();

        ThemeService.Apply(AppTheme.Dark, merged, FakeLoad, NotHighContrast);

        var added = Assert.Single(merged);
        Assert.Equal(AppTheme.Dark, added[MarkerKey]);
    }

    [Fact]
    public void Apply_replaces_a_previously_applied_theme_instead_of_stacking()
    {
        var merged = new Collection<ResourceDictionary>();
        ThemeService.Apply(AppTheme.Nebula, merged, FakeLoad, NotHighContrast);

        ThemeService.Apply(AppTheme.Light, merged, FakeLoad, NotHighContrast);

        var remaining = Assert.Single(merged);
        Assert.Equal(AppTheme.Light, remaining[MarkerKey]);
    }

    [Fact]
    public void Apply_leaves_non_theme_dictionaries_untouched()
    {
        var controls = new ResourceDictionary();
        var merged = new Collection<ResourceDictionary> { controls };
        ThemeService.Apply(AppTheme.Nebula, merged, FakeLoad, NotHighContrast);

        ThemeService.Apply(AppTheme.Terminal, merged, FakeLoad, NotHighContrast);

        Assert.Equal(2, merged.Count);
        Assert.Contains(controls, merged);
    }

    [Fact]
    public void Apply_removes_the_unmarked_default_dictionary_App_xaml_starts_with()
    {
        // App.xaml merges Themes/Nebula.xaml directly at startup, with no marker resource of its
        // own (unlike every dictionary Apply itself ever loads) - this stands in for that default.
        var unmarkedDefault = FakeSourcedDictionary(new Uri("Themes/Nebula.xaml", UriKind.Relative));
        var merged = new Collection<ResourceDictionary> { unmarkedDefault };

        ThemeService.Apply(AppTheme.Dark, merged, FakeLoad, NotHighContrast);
        ThemeService.Apply(AppTheme.Light, merged, FakeLoad, NotHighContrast);

        var remaining = Assert.Single(merged);
        Assert.Equal(AppTheme.Light, remaining[MarkerKey]);
    }

    [Fact]
    public void Apply_loads_the_high_contrast_dictionary_instead_of_the_requested_theme_when_high_contrast_is_on()
    {
        var merged = new Collection<ResourceDictionary>();
        Uri? requestedUri = null;
        ResourceDictionary Recording(Uri uri) { requestedUri = uri; return new ResourceDictionary(); }

        ThemeService.Apply(AppTheme.Dark, merged, Recording, IsHighContrast);

        Assert.EndsWith("HighContrast.xaml", requestedUri!.OriginalString);
        // The marker still records the originally requested theme, so a later high-contrast-off
        // re-apply can restore it - only the token source changes, not what the user picked.
        var added = Assert.Single(merged);
        Assert.Equal(AppTheme.Dark, added[MarkerKey]);
    }

    [Fact]
    public void Apply_reapplied_with_the_same_theme_after_a_live_high_contrast_change_swaps_to_the_high_contrast_dictionary_with_no_duplicate()
    {
        var merged = new Collection<ResourceDictionary>();
        var highContrastNow = false;

        ThemeService.Apply(AppTheme.Dark, merged, FakeSourcedDictionary, () => highContrastNow);
        highContrastNow = true;
        ThemeService.Apply(AppTheme.Dark, merged, FakeSourcedDictionary, () => highContrastNow);

        var remaining = Assert.Single(merged);
        Assert.EndsWith("HighContrast.xaml", remaining.Source!.OriginalString);
    }

    /// <summary>Window opacity moved to <see cref="WindowOpacity"/>'s own native, per-HWND alpha (see
    /// <see cref="WindowOpacityTests"/>) - Bg.Window is always fully opaque now, whatever the slider
    /// says, since the brush is no longer where translucency comes from.</summary>
    [Fact]
    public void Apply_builds_an_always_opaque_window_background_brush_from_the_active_theme_s_base_colour()
    {
        var merged = new Collection<ResourceDictionary>();

        ThemeService.Apply(AppTheme.Dark, merged, FakeLoadWithBaseColor, NotHighContrast);

        var added = Assert.Single(merged);
        var windowBrush = Assert.IsType<SolidColorBrush>(added["Bg.Window"]);
        Assert.Equal(FakeBaseColor, windowBrush.Color);
        Assert.Equal(1.0, windowBrush.Opacity);
    }

    [Theory]
    [InlineData(AppTheme.System, false, true, ResolvedTheme.Light)]
    [InlineData(AppTheme.System, false, false, ResolvedTheme.Dark)]
    [InlineData(AppTheme.System, true, true, ResolvedTheme.HighContrast)]
    [InlineData(AppTheme.Nebula, true, true, ResolvedTheme.HighContrast)]
    [InlineData(AppTheme.Nebula, false, true, ResolvedTheme.Nebula)]
    [InlineData(AppTheme.Nebula, false, false, ResolvedTheme.Nebula)]
    [InlineData(AppTheme.Terminal, false, true, ResolvedTheme.Terminal)]
    [InlineData(AppTheme.Dark, false, true, ResolvedTheme.Dark)]
    [InlineData(AppTheme.Light, false, false, ResolvedTheme.Light)]
    public void Resolve_picks_the_token_set_that_actually_renders(
        AppTheme requested, bool highContrast, bool windowsUsesLightTheme, ResolvedTheme expected)
    {
        Assert.Equal(expected, ThemeService.Resolve(requested, highContrast, windowsUsesLightTheme));
    }
}
