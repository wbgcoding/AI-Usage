using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace AiUsage.Services;

public enum AppTheme { Nebula, Terminal, Dark, Light, System }

/// <summary>What <see cref="ThemeService.Resolve"/> actually renders once <see cref="AppTheme.System"/>
/// and Windows high-contrast mode are both taken into account - a separate type from
/// <see cref="AppTheme"/> because "high contrast" and "follows Windows" are never something the user
/// picks directly, only an outcome of a pick plus the current system state.</summary>
public enum ResolvedTheme { Nebula, Terminal, Dark, Light, HighContrast }

/// <summary>
/// Swaps exactly one theme ResourceDictionary in the application's merged dictionaries
/// - layout and control templates never change, only the 16 token values.
/// No window is ever recreated; every control re-renders through its existing DynamicResource
/// bindings. While Windows high-contrast mode is on, the tokens come from Themes/HighContrast.xaml
/// (SystemColors) instead of the requested theme, snapshotted at the moment Apply runs.
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<AppTheme, Uri> ThemeUris = new()
    {
        [AppTheme.Nebula] = new Uri("Themes/Nebula.xaml", UriKind.Relative),
        [AppTheme.Terminal] = new Uri("Themes/Terminal.xaml", UriKind.Relative),
        [AppTheme.Dark] = new Uri("Themes/Dark.xaml", UriKind.Relative),
        [AppTheme.Light] = new Uri("Themes/Light.xaml", UriKind.Relative),
    };

    private static readonly Uri HighContrastUri = new("Themes/HighContrast.xaml", UriKind.Relative);

    private static readonly IReadOnlyList<Uri> KnownThemeUris = [.. ThemeUris.Values, HighContrastUri];

    private const string WindowBrushKey = "Bg.Window";
    private const string BaseBrushKey = "Bg.Base";

    // Identifies "the current theme dictionary" by an explicit marker resource rather than
    // parsing/comparing Uri strings - robust to relative-vs-absolute pack URI formatting, and
    // lets tests swap a dictionary in without any real WPF resource I/O.
    private const string MarkerKey = "AiUsage.ThemeService.ActiveTheme";

    /// <summary>The theme most recently requested through <see cref="Apply"/> (never the resolved
    /// System/high-contrast outcome) - an ambient singleton for the same reason
    /// <see cref="LocalizationService.Instance"/> is one: <see cref="SupportReport"/> is handed only a
    /// tile list, with no settings reference of its own to read <see cref="Models.AppSettings.Theme"/>
    /// from directly.</summary>
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public static AppTheme ParseTheme(string name) =>
        Enum.TryParse<AppTheme>(name, ignoreCase: true, out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.Nebula;

    /// <summary>Pure decision behind "which token set actually renders": high contrast always wins
    /// (a translucent, colour-picked window is exactly wrong once the user relies on system colours
    /// for readability), otherwise <see cref="AppTheme.System"/> follows Windows' own light/dark
    /// choice and every other value maps straight through.</summary>
    public static ResolvedTheme Resolve(AppTheme requested, bool highContrast, bool windowsUsesLightTheme)
    {
        if (highContrast)
            return ResolvedTheme.HighContrast;

        return requested switch
        {
            AppTheme.System => windowsUsesLightTheme ? ResolvedTheme.Light : ResolvedTheme.Dark,
            AppTheme.Terminal => ResolvedTheme.Terminal,
            AppTheme.Dark => ResolvedTheme.Dark,
            AppTheme.Light => ResolvedTheme.Light,
            _ => ResolvedTheme.Nebula,
        };
    }

    private static Uri UriFor(ResolvedTheme resolved) => resolved switch
    {
        ResolvedTheme.Terminal => ThemeUris[AppTheme.Terminal],
        ResolvedTheme.Dark => ThemeUris[AppTheme.Dark],
        ResolvedTheme.Light => ThemeUris[AppTheme.Light],
        ResolvedTheme.HighContrast => HighContrastUri,
        _ => ThemeUris[AppTheme.Nebula],
    };

    public static void Apply(AppTheme theme)
    {
        Apply(theme, Application.Current.Resources.MergedDictionaries, uri => new ResourceDictionary { Source = uri },
            () => SystemParameters.HighContrast, IsWindowsUsingLightTheme);
        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Raised after a theme swap on the live resources. A theme can change fonts and with
    /// them the height of the title bar and the tiles, so a window that sizes itself to its content
    /// measures again.</summary>
    public static event EventHandler? Applied;

    /// <summary>Test seam: swap into an injected dictionary collection via an injected loader and HC check.
    /// <see cref="AppTheme.System"/> resolves as if Windows were on its own light theme - none of the
    /// existing callers of this four-argument overload ever request <see cref="AppTheme.System"/>, so
    /// this default never actually matters to them.</summary>
    internal static void Apply(AppTheme theme, Collection<ResourceDictionary> mergedDictionaries, Func<Uri, ResourceDictionary> load, Func<bool> isHighContrast) =>
        Apply(theme, mergedDictionaries, load, isHighContrast, () => true);

    /// <summary>Test seam: as above, plus an injected "is Windows using its light theme" check for
    /// <see cref="AppTheme.System"/>.</summary>
    internal static void Apply(AppTheme theme, Collection<ResourceDictionary> mergedDictionaries, Func<Uri, ResourceDictionary> load, Func<bool> isHighContrast, Func<bool> isWindowsUsingLightTheme)
    {
        // Two ways an old theme dictionary can be sitting in the collection: marked by a previous
        // Apply, or App.xaml's own unmarked Nebula default that started the process - without the
        // second check the very first switch away from Nebula appends instead of replacing, leaving
        // an orphan behind for the rest of the process.
        foreach (var stale in mergedDictionaries.Where(IsThemeDictionary).ToList())
            mergedDictionaries.Remove(stale);

        var highContrast = isHighContrast();
        var resolved = Resolve(theme, highContrast, isWindowsUsingLightTheme());
        var uri = UriFor(resolved);
        var dictionary = load(uri);
        // The marker records the originally requested theme, not the resolution - a later
        // high-contrast-off re-apply, or a system light/dark change, needs to know what the user
        // actually picked, not what last happened to render.
        dictionary[MarkerKey] = theme;
        mergedDictionaries.Add(dictionary);
        CurrentTheme = theme;

        // Built here, from whichever base colour just became active, rather than baked into each
        // theme file: a value living in a theme file could not be swapped out per-call. Always fully
        // opaque - the adjustable opacity setting is no longer a brush-level concern at all (see
        // WindowOpacity), which applies it as native, per-window DWM alpha instead.
        if (dictionary[BaseBrushKey] is SolidColorBrush baseBrush)
        {
            var windowBrush = new SolidColorBrush(baseBrush.Color);
            windowBrush.Freeze();
            dictionary[WindowBrushKey] = windowBrush;
        }
    }

    /// <summary>Reads <c>HKCU\...\Personalize\AppsUseLightTheme</c> directly rather than through
    /// <see cref="SystemParameters"/> (WPF exposes no such property) - 0 means dark, 1 or a missing
    /// value means light, matching the key's own documented default.</summary>
    /// <summary>Internal, not private: the Settings window's own "follow Windows" swatch needs the
    /// same live answer to show a preview that matches what <see cref="Apply"/> would actually pick.</summary>
    internal static bool IsWindowsUsingLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Contains(MarkerKey) ||
        (dictionary.Source is { } source && KnownThemeUris.Any(u => u.OriginalString == source.OriginalString));
}
