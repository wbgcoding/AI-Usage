namespace AiUsage.Models;

/// <summary>All persisted settings (settings.json version 1).</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public static readonly IReadOnlyList<string> KnownProviderIds = ["claude", "codex", "cursor", "gemini", "copilot"];

    /// <summary>The widget's own day-grid tile, never a real provider: its own entry in <see
    /// cref="Providers"/> (<see cref="ProviderSettings.Order"/>/<see cref="ProviderSettings.Visible"/>
    /// only, the rest unused) so it shares the exact ordering and hiding mechanism every provider tile
    /// already uses instead of a second one - deliberately left out of <see cref="KnownProviderIds"/>
    /// and <see cref="Accounts"/>, so <see cref="Storage.SettingsStore"/>'s own provider bookkeeping
    /// never touches it.</summary>
    public const string DayGridTileId = "daygrid";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Floor for a web-backed provider's own refresh interval (the Claude web session), in
    /// minutes - never polled more often than this, whatever <see cref="RefreshSeconds"/> says. Settings
    /// UI for this value lands in a later step; today it is the fetch-side clamp only.</summary>
    public int RemoteRefreshMinutes { get; set; } = 5;

    public bool AlwaysOnTop { get; set; }

    public string Layout { get; set; } = "Vertical";

    public string Theme { get; set; } = "System";

    /// <summary>How opaque the window background is, 0-100; the window is never fully see-through
    /// even at the lowest setting. Ignored (treated as 100) while Windows high-contrast mode is
    /// on. See <see cref="Services.SettingsRanges.ClampWindowOpacityPercent"/> for the valid
    /// range.</summary>
    public int WindowOpacityPercent { get; set; } = 100;

    public string Language { get; set; } = "System";

    public bool Autostart { get; set; }

    public int HistoryRetentionDays { get; set; } = 365;

    public string ChartRange { get; set; } = "Day";

    /// <summary>Whether the history chart draws last week's figures as a faint comparison line
    /// behind the current one. Defaults on: comparing a five-hour window against the same clock time
    /// a week ago is usually the more useful reading, not an opt-in extra.</summary>
    public bool ShowPreviousWeekLine { get; set; } = true;

    /// <summary>Whether hovering a control shows its explanatory tooltip anywhere in the program -
    /// enforced centrally by one class handler on the tooltip-opening event (see App.xaml.cs
    /// OnStartup), not per element. Defaults on. No longer offered in the Settings window; kept so a
    /// hand-edited settings.json can still switch tooltips off.</summary>
    public bool ShowTooltips { get; set; } = true;

    public string TileDensity { get; set; } = "Auto";

    /// <summary>How the tiles are ordered: "Custom" keeps the order the person arranged (<see
    /// cref="ProviderSettings.Order"/>), "ByUsage" puts the tile with the highest color level on top.
    /// The own order stays stored either way, so switching back restores it.</summary>
    public string TileOrderMode { get; set; } = "Custom";

    /// <summary>Set the first time the day-grid tile is shown. That first reveal moves it to the
    /// top; a missing settings entry cannot tell this apart, since reordering any tile writes an
    /// entry for every row, the hidden day grid included.</summary>
    public bool DayGridShownOnce { get; set; }

    /// <summary>Whose usage the tray icon shows: "Auto" for the highest across every visible tile,
    /// else one tile's provider id (an extra account's carries its "#n"), which the icon then always
    /// shows - falling back to the highest while that tile is hidden, gone or has no numbers.</summary>
    public string TrayProvider { get; set; } = "Auto";

    /// <summary>Which of <see cref="TrayProvider"/>'s windows the tray icon shows: "Auto" for the
    /// highest, else a <see cref="WindowKind"/> name ("FiveHour", "Weekly", "Other"), falling back to
    /// the highest while the shown tiles report no window of that kind.</summary>
    public string TrayWindow { get; set; } = "Auto";

    /// <summary>The notification threshold every provider uses unless its own row ticks
    /// <see cref="ThresholdSettings.UseCustom"/> - one decision most people only ever make once,
    /// instead of the same slider repeated per provider and window.</summary>
    public double DefaultThreshold { get; set; } = 85;

    public bool DefaultThresholdEnabled { get; set; } = true;

    /// <summary>Master switch for the second stream of balloons ("a window is free again") - default
    /// on, same as the threshold notifications above; a provider's own <see
    /// cref="ProviderSettings.NotifyOnResetEnabled"/> still gates its own reset balloon underneath
    /// this one.</summary>
    public bool NotifyOnReset { get; set; } = true;

    /// <summary>Whether a tile marks itself once its own session file's newest turn reads as
    /// "waiting for the user" (see <see cref="Providers.Parsing.AttentionDetector"/>). Defaults on:
    /// unlike the reset balloon above, this is a passive marker on a surface already open, not an
    /// interruption, so there is no reason to make it opt-in.</summary>
    public bool ShowAttentionMark { get; set; } = true;

    /// <summary>How old a finished turn is allowed to get before <see
    /// cref="ShowAttentionMark"/> stops calling it "waiting" (see <see
    /// cref="Providers.Parsing.AttentionDetector.IsWaiting"/>) - two hours by default, clamped by
    /// <see cref="Services.SettingsRanges.ClampAttentionMaxAgeMinutes"/> to between 5 and 1440
    /// minutes.</summary>
    public int AttentionMaxAgeMinutes { get; set; } = 120;

    public bool QuietHoursEnabled { get; set; }

    /// <summary>"HH:mm", parsed with <see cref="Services.QuietHours"/> - a string keeps the file
    /// readable and culture-free, unlike a raw <see cref="TimeOnly"/> serialization.</summary>
    public string QuietHoursStart { get; set; } = "22:00";

    public string QuietHoursEnd { get; set; } = "08:00";

    /// <summary>True once the one-time "still running in the tray" balloon has been shown.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>True once the one-time welcome window (<see cref="Services.StartupMode"/>) has been
    /// shown and closed - never shown again after that, on this profile.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Account key -> the usage endpoint discovered once for that web-backed account, so
    /// later ticks fetch it directly instead of re-running discovery. Per account because the path
    /// carries an account-specific id (Claude's organisation, Codex's user).</summary>
    public Dictionary<string, string> WebUsagePaths { get; set; } = [];

    /// <summary>Off by default: a system-wide shortcut is a deliberate opt-in, not a surprise
    /// keyboard grab on first launch.</summary>
    public bool HotkeyEnabled { get; set; }

    /// <summary>"Ctrl+Alt+U"-style text, parsed by <see cref="Services.GlobalHotkey.TryParse"/>.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+U";

    /// <summary>Opt-out: the app ships as a file, not through a store, so this is the only way a
    /// user ever learns a newer version exists. See <see cref="Services.UpdateCheck"/> - the check
    /// itself only reads; installing is a separate, signature-gated step.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Off by default: makes the window ignore the mouse entirely (<c>WS_EX_TRANSPARENT</c>).
    /// See <see cref="Services.ClickThroughPolicy"/> for the always-on-top/opacity implications this
    /// forces while it is on.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>True once the one-time click-through warning balloon has been shown.</summary>
    public bool ClickThroughHintShown { get; set; }

    /// <summary>Null until the first check ever runs. <see cref="Services.UpdateCheck.CheckIfDueAsync"/>
    /// sets this on every attempt, successful or not, so a persistent failure (offline, or the
    /// repository still being private) is retried at most once a day rather than on every window
    /// open.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>The tag of the newest release the last successful check found, kept so the notice
    /// in the main window survives a restart between two daily checks. Null when none is known or
    /// the newest one is not newer than the running version.</summary>
    public string? KnownLatestTag { get; set; }

    /// <summary>The release page of <see cref="KnownLatestTag"/>.</summary>
    public string? KnownLatestUrl { get; set; }

    /// <summary>Section key (<see cref="Views.Controls.CollapsibleSection.SectionKey"/>) -> whether
    /// that section of the statistics window is collapsed. A key missing here - including on an
    /// older settings file written before this property existed - counts as expanded; <see
    /// cref="Views.StatsWindow"/> is the only reader/writer.</summary>
    public Dictionary<string, bool> StatsSectionsCollapsed { get; set; } = [];

    /// <summary>How the statistics window arranges its sections: rows of one full-width section or two
    /// columns side by side. Null = the default arrangement. Whoever reads it passes it through <see
    /// cref="Stats.StatsLayout.Normalize"/> first; <see cref="Views.StatsWindow"/> is the only
    /// reader/writer.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(Stats.TolerantLayoutConverter))]
    public List<Stats.StatsLayoutRow>? StatsSectionLayout { get; set; }

    /// <summary>Which of the "Per day" panel's three views (<c>Day</c>/<c>Weekday</c>/<c>Hour</c>,
    /// <see cref="Stats.StatsPerDayView"/> stringified) the statistics window last showed. Stored as
    /// a plain string, not the enum itself, so an older or newer build's own settings file round
    /// trips a value it does not recognise instead of failing to parse; an unrecognised value is
    /// treated the same as a missing one. <see cref="Views.StatsWindow"/> is the only reader/writer.
    /// </summary>
    public string StatsPerDayView { get; set; } = "Day";

    public WindowSettings Window { get; set; } = new();

    public Dictionary<string, ProviderSettings> Providers { get; set; } = CreateDefaultProviders();

    /// <summary>Account key -> provider id. The first/only account of every provider is implicit -
    /// each provider's own id maps to itself (see <see cref="CreateDefaultAccounts"/>) - so an
    /// existing settings.json with no "accounts" property at all still loads correctly with no
    /// migration. A further entry is added only by the Settings window's account-add button, and only
    /// for a provider that signs in through a web session (see <see
    /// cref="Services.IUsageProvider.SupportsInAppSignIn"/>) - the other providers never get a second
    /// entry here because a second local session folder for them is not a thing that exists.</summary>
    public Dictionary<string, string> Accounts { get; set; } = CreateDefaultAccounts();

    /// <summary>Account key -> the GitHub login a further Copilot account reads through (see
    /// <see cref="Services.ProviderRegistry.CreateCopilotAccount"/>) - Copilot's own accounts have no
    /// browser profile or web session of their own, only this one extra fact: which of the GitHub
    /// CLI's already signed-in users this account asks <c>gh</c> to read as. The primary account
    /// ("copilot") is never in here - it always reads as the CLI's own active user, exactly as
    /// before.</summary>
    public Dictionary<string, string> CopilotAccountLogins { get; set; } = [];

    public static Dictionary<string, ProviderSettings> CreateDefaultProviders() =>
        KnownProviderIds.ToDictionary(id => id, _ => new ProviderSettings());

    /// <summary>The one account every install starts with: the first known provider's own id, mapped
    /// to itself (see <see cref="Accounts"/>) - written via <see cref="KnownProviderIds"/>'s own first
    /// entry rather than repeated here, so this and the known-provider list can never name two
    /// different providers as "the one that can have more than one account".</summary>
    public static Dictionary<string, string> CreateDefaultAccounts() => new() { [KnownProviderIds[0]] = KnownProviderIds[0] };
}

public sealed class WindowSettings
{
    public double Left { get; set; } = 100;

    public double Top { get; set; } = 100;

    /// <summary>The Settings window's own remembered width, not the main window's - both windows
    /// share this settings class, so this one property belongs to a different window than the rest.</summary>
    public double SettingsWidth { get; set; } = 560;

    /// <summary>The Stats window's own remembered width and height, 0 meaning "never resized" -
    /// falls back to the window's fixed 720 x 520 default the same way a remembered position that no
    /// longer fits a monitor does. See <see cref="Services.WindowPlacementService.RememberedStatsWindowSize"/>.</summary>
    public double StatsWidth { get; set; }

    public double StatsHeight { get; set; }

    public string MonitorDeviceName { get; set; } = @"\\.\DISPLAY1";

    public bool Collapsed { get; set; }

    /// <summary>A row of tiles and a column of tiles almost never want the same width, so each
    /// arrangement remembers its own size instead of fighting over one shared pair of numbers.
    /// Position (<see cref="Left"/>, <see cref="Top"/>, <see cref="MonitorDeviceName"/>) and
    /// <see cref="Collapsed"/> stay shared above, since those describe where the window is, not how
    /// big its content wants to be.</summary>
    public LayoutSizes Vertical { get; set; } = new();

    public LayoutSizes Horizontal { get; set; } = new();
}

/// <summary>One arrangement's (vertical or horizontal) own remembered width/height, kept apart from
/// the other arrangement's - see <see cref="WindowSettings.Vertical"/>.</summary>
public sealed class LayoutSizes
{
    public double Width { get; set; } = 340;

    /// <summary>True once the user has dragged the width by hand; the window then keeps that width
    /// instead of resizing itself to the tiles again.</summary>
    public bool WidthIsManual { get; set; }

    /// <summary>Null while the height follows the content automatically.</summary>
    public double? Height { get; set; }
}

public sealed class ProviderSettings
{
    public bool Visible { get; set; } = true;

    /// <summary>Tile position in the widget - lower sorts first. Stays -1 (never a real position)
    /// until <see cref="Storage.SettingsStore"/> assigns it from this provider's own index in <see
    /// cref="AppSettings.KnownProviderIds"/>, which happens for every entry on every <see
    /// cref="Storage.SettingsStore.Load"/> - a value stored in settings.json always wins over that
    /// default afterward.</summary>
    public int Order { get; set; } = -1;

    public ThresholdSettings Thresholds { get; set; } = new();

    /// <summary>Whether the tile's own 5-hour row shows at all - independent of <see
    /// cref="Visible"/>, which hides the whole tile. History keeps recording this window either
    /// way; only the row disappears.</summary>
    public bool ShowFiveHour { get; set; } = true;

    /// <summary>Covers the weekly row only. An <see cref="WindowKind.Other"/> row is no longer a
    /// refinement of this one - each is governed on its own by <see cref="HiddenWindows"/>, since a
    /// provider can report several distinct Other rows (Cursor's model breakdown, for instance) that
    /// a user may want to hide one at a time.</summary>
    public bool ShowWeekly { get; set; } = true;

    /// <summary>Whether this tile leaves out its history chart at Full density. The bars, numbers and
    /// states stay; the tile just gets shorter, like one that has no history yet. Default off: every
    /// chart shows.</summary>
    public bool ChartHidden { get; set; }

    /// <summary>Whether this tile never gets the "agent is waiting" mark, even while <see
    /// cref="AppSettings.ShowAttentionMark"/> is on. Default off: every provider that can be
    /// detected is marked.</summary>
    public bool AttentionDisabled { get; set; }

    /// <summary>Row labels (<see cref="Models.UsageWindow.Label"/>, a resource key or a literal
    /// provider-supplied name) hidden from this tile - only ever applies to <see
    /// cref="WindowKind.Other"/> rows, which is the one kind a provider can report more than one of.
    /// Empty by default: every row a provider reports starts visible.</summary>
    public List<string> HiddenWindows { get; set; } = [];

    /// <summary>Master switch for this one tile's own notifications - off suppresses both its
    /// threshold and reset balloons, whatever <see cref="ThresholdSettings"/> and <see
    /// cref="NotifyOnResetEnabled"/> below say. Default on, same as every other notification
    /// setting.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>This one tile's own reset-balloon switch, gated underneath both <see
    /// cref="NotificationsEnabled"/> and the global <see cref="AppSettings.NotifyOnReset"/> - default
    /// on, same as the other two.</summary>
    public bool NotifyOnResetEnabled { get; set; } = true;

    /// <summary>Set by the sign-out action (Settings.SignOut), cleared by the sign-in action
    /// (Settings.SignIn) - while true, the scheduler reads this account through no source at all (no
    /// web session, no local file, no CLI call), keyed by account so a further account of the same
    /// provider can be disconnected on its own. Never signs the underlying tool itself out - only
    /// this app stops reading it. Default false, so an existing settings.json with no such key loads
    /// every account connected.</summary>
    public bool Disconnected { get; set; }
}

public sealed class ThresholdSettings
{
    /// <summary>False (the default) means this provider follows <see
    /// cref="AppSettings.DefaultThreshold"/>/<see cref="AppSettings.DefaultThresholdEnabled"/>
    /// instead of the three values below - most providers never need their own.</summary>
    public bool UseCustom { get; set; }

    public double FiveHour { get; set; } = 85;

    public bool FiveHourEnabled { get; set; } = true;

    public double Weekly { get; set; } = 85;

    public bool WeeklyEnabled { get; set; } = true;

    /// <summary>Covers any window kind beyond the two above (<see cref="WindowKind.Other"/>) -
    /// Gemini's and Copilot's own windows, and a provider's per-model windows.</summary>
    public double Other { get; set; } = 85;

    public bool OtherEnabled { get; set; } = true;
}
