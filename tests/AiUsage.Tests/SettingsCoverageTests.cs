using System.Reflection;
using AiUsage.Models;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Reset and import go through one copy list in the settings view model; a new setting that
/// is in neither list below was forgotten and fails here.</summary>
public class SettingsCoverageTests
{
    private static readonly HashSet<string> CopiedByResetAndImport =
    [
        nameof(AppSettings.RefreshSeconds), nameof(AppSettings.RemoteRefreshMinutes), nameof(AppSettings.WindowLayer),
        nameof(AppSettings.Layout), nameof(AppSettings.TileOrderMode), nameof(AppSettings.Theme),
        nameof(AppSettings.Language), nameof(AppSettings.Autostart), nameof(AppSettings.HistoryRetentionDays),
        nameof(AppSettings.WindowOpacityPercent), nameof(AppSettings.MicaEnabled), nameof(AppSettings.ChartRange),
        nameof(AppSettings.ShowPreviousWeekLine), nameof(AppSettings.ShowPaceMarker), nameof(AppSettings.HideOnFullscreen), nameof(AppSettings.ZoomPercent),
        nameof(AppSettings.ShowTooltips), nameof(AppSettings.TileDensity), nameof(AppSettings.DayGridShownOnce),
        nameof(AppSettings.DefaultThreshold), nameof(AppSettings.DefaultThresholdEnabled), nameof(AppSettings.NotifyOnReset),
        nameof(AppSettings.ForecastAlertEnabled), nameof(AppSettings.ForecastLeadMinutes), nameof(AppSettings.LimitReachedAlertEnabled),
        nameof(AppSettings.ShowAttentionMark), nameof(AppSettings.AttentionMaxAgeMinutes), nameof(AppSettings.CheckForUpdates),
        nameof(AppSettings.SaveEnergyOnBattery), nameof(AppSettings.QuietHoursEnabled), nameof(AppSettings.AllDaysAllowed), nameof(AppSettings.QuietDays),
        nameof(AppSettings.QuietHoursStart), nameof(AppSettings.QuietHoursEnd), nameof(AppSettings.HotkeyEnabled),
        nameof(AppSettings.Hotkey), nameof(AppSettings.ClickThrough), nameof(AppSettings.TrayProvider),
        nameof(AppSettings.TrayWindow), nameof(AppSettings.StatsSectionLayout), nameof(AppSettings.StatsSectionsCollapsed),
        nameof(AppSettings.Providers),
        // The token usage window's view choices are preferences, so they travel too.
        nameof(AppSettings.StatsRange), nameof(AppSettings.StatsGrouping), nameof(AppSettings.StatsColorBy),
        nameof(AppSettings.StatsProvider), nameof(AppSettings.StatsCustomFrom), nameof(AppSettings.StatsCustomTo),
    ];

    private static readonly HashSet<string> KeptOnPurpose =
    [
        nameof(AppSettings.SchemaVersion), // file format, not a preference
        nameof(AppSettings.MachineId), // identifies this PC
        nameof(AppSettings.ImportedMachines), // histories imported onto this PC
        nameof(AppSettings.LegacyAlwaysOnTop), // read once on load, never written
        nameof(AppSettings.LegacyStatsPerDayView), // read once on load, never written
        nameof(AppSettings.LegacyQuietWeekend), // read once on load, never written
        nameof(AppSettings.TrayHintShown), // one-time hint already seen on this PC
        nameof(AppSettings.WelcomeShown), // one-time hint already seen on this PC
        nameof(AppSettings.ClickThroughHintShown), // one-time hint already seen on this PC
        nameof(AppSettings.WebUsagePaths), // paths on this PC
        nameof(AppSettings.LastUpdateCheckUtc), // update check state of this PC
        nameof(AppSettings.KnownLatestTag), // update check state of this PC
        nameof(AppSettings.KnownLatestUrl), // update check state of this PC
        nameof(AppSettings.Window), // every window rectangle fits only this screen
        nameof(AppSettings.Accounts), // account bindings come from sign-ins on this PC
        nameof(AppSettings.CopilotAccountLogins), // account bindings come from sign-ins on this PC
    ];

    [Fact]
    public void Every_setting_is_either_copied_or_kept_on_purpose()
    {
        var overlap = CopiedByResetAndImport.Intersect(KeptOnPurpose).ToList();
        Assert.True(overlap.Count == 0, $"Listed as copied and as kept: {string.Join(", ", overlap)}");

        var actual = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .ToHashSet();
        var listed = CopiedByResetAndImport.Union(KeptOnPurpose).ToHashSet();

        var missing = actual.Except(listed).OrderBy(name => name).ToList();
        var unknown = listed.Except(actual).OrderBy(name => name).ToList();
        Assert.True(missing.Count == 0, $"AppSettings property in neither list (copy it in ApplyLoaded or keep it on purpose): {string.Join(", ", missing)}");
        Assert.True(unknown.Count == 0, $"Listed but not an AppSettings property: {string.Join(", ", unknown)}");
    }
}
