using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace AiUsage.Services;

/// <summary>
/// The one place every visible piece of text comes from. Deliberately
/// bypasses the normal ResourceManager satellite-assembly/culture-fallback mechanism: both
/// <c>Strings.resx</c> (EN) and <c>Strings.de.resx</c> (DE) are embedded straight into the main
/// assembly (see AiUsage.csproj), each behind its own <see cref="ResourceManager"/> instance, so
/// switching languages is a plain lookup-table swap - no thread culture, no satellite assembly
/// resolution, nothing that could interact with number/date formatting (those follow
/// the OS regional format regardless of which UI language is picked here).
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    // Declared before Instance below: static field initializers run top-to-bottom in source order,
    // and Instance's own initializer runs this class's constructor immediately - which reads this
    // table via _active's own initializer (through ResourceManagerFor). Declaring Instance first
    // would leave the table still null at that point, throwing a NullReferenceException on the very
    // first lookup.
    //
    // The one place a language is registered - adding a fourth entry is one line here plus one resx
    // file. "System" carries no ResourceManager of its own: it is never a real language, only a
    // request to resolve one of the concrete entries below from the OS UI culture (see
    // ResourceManagerFor). Order matters: System first, English second, the rest alphabetical by
    // their own display name - LanguageChoices in SettingsViewModel is built straight from this list.
    public static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("System", "Language.System", null),
        new("en", "Language.English", new ResourceManager("AiUsage.Resources.Strings", typeof(LocalizationService).Assembly)),
        new("de", "Language.German", new ResourceManager("AiUsage.Resources.Strings.de", typeof(LocalizationService).Assembly)),
    ];

    /// <summary>The one instance every window/view-model binds against - same pattern as
    /// <see cref="ThemeService"/> applying to the one live <c>Application.Current</c>.</summary>
    public static LocalizationService Instance { get; } = new();

    // German until App.xaml.cs's own SetLanguage(settings.Language) call resolves the real choice at
    // startup - see the constructor comment below for why this is not "System" by default.
    private ResourceManager _active = ResourceManagerFor("de");

    public event PropertyChangedEventHandler? PropertyChanged;

    // Deliberately NOT "System"-by-default (see _active's own field comment above): every view model
    // that never runs inside a live App (every unit test in this project) would otherwise read
    // whichever language the machine running the test suite happens to have installed -
    // non-deterministic, and every test written before this file existed already asserts on the
    // German wording that was the app's only text back then.
    private LocalizationService()
    {
    }

    /// <summary>"System", "de" or "en" (AppSettings.Language). "System" resolves
    /// once, at the moment this is called, from <see cref="CultureInfo.CurrentUICulture"/> - not
    /// re-evaluated later, so a language switch always needs an explicit call, never a background
    /// poll of the OS setting.</summary>
    public void SetLanguage(string language)
    {
        _active = ResourceManagerFor(language);
        // WPF's binding engine treats "Item[]" as "every indexer binding on this source may have
        // changed" - this is what makes a language switch apply to the whole open window at once,
        // with no window recreation and no per-control refresh call.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Empty for an unknown key rather than throwing - a placeholder tile must never crash
    /// the window over a typo'd resource key (same contract the old StatusTextMap.Resolve had).</summary>
    public string this[string key] => _active.GetString(key, CultureInfo.InvariantCulture) ?? "";

    /// <summary>The active UI language's own display name ("English"/"Deutsch") - "System" resolved
    /// the same way <see cref="SetLanguage"/> already resolves it, never the literal setting value.
    /// Used by <see cref="SupportReport"/>, which has no other way to name the active language: it is
    /// handed only a tile list, exactly like every other view model reads this ambient singleton
    /// instead of a language parameter of its own.</summary>
    public string ActiveLanguageDisplayName => this[Languages.First(l => l.Resources == _active).LabelKey];

    // CurrentCulture, not InvariantCulture: numbers and dates embedded into a
    // format string follow the OS regional format, independent of which UI language is active.
    public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>Internal rather than private so a test can resolve a language without going
    /// through the shared instance.</summary>
    internal static ResourceManager ResourceManagerFor(string language)
    {
        // Case-insensitive: the setting itself always stores a lowercase code, but settings.json is
        // a plain text file, and a hand-typed "DE" should not silently fall through to "System".
        var concrete = Languages.FirstOrDefault(l =>
            l.Resources is not null && string.Equals(l.Code, language, StringComparison.OrdinalIgnoreCase));
        if (concrete is not null)
            return concrete.Resources!;

        // "System" (or anything unrecognised): the language Windows is displayed in right now, which
        // a user can change at any time - not the one Windows was set up with, which a fixed choice
        // would otherwise be stuck on. Falls back to the first registered language (English) when
        // that OS language has no resources of its own here.
        var systemCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.FirstOrDefault(l => l.Resources is not null &&
            string.Equals(l.Code, systemCode, StringComparison.OrdinalIgnoreCase))?.Resources
            ?? Languages.First(l => l.Resources is not null).Resources!;
    }
}

/// <summary>One registered UI language: its stored settings code, the resx key naming it in its own
/// picker, and the <see cref="ResourceManager"/> that answers lookups for it - null for "System",
/// which is never a real language of its own (see <see cref="LocalizationService.Languages"/>).</summary>
public sealed record LanguageOption(string Code, string LabelKey, ResourceManager? Resources);
