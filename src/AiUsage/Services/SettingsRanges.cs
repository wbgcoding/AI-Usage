using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Pure clamp/snap rules for the Settings window's sliders. Kept separate from <see cref="ViewModels.SettingsViewModel"/> so
/// the boundary cases stay trivially testable without any WPF or settings-store plumbing.
/// </summary>
public static class SettingsRanges
{
    public const int MinRefreshSeconds = 15;
    public const int MaxRefreshSeconds = 900; // 15 minutes

    public static int ClampRefreshSeconds(int seconds) => Math.Clamp(seconds, MinRefreshSeconds, MaxRefreshSeconds);

    public const int MinRemoteRefreshMinutes = 1;
    public const int MaxRemoteRefreshMinutes = 60;

    public static int ClampRemoteRefreshMinutes(int minutes) => Math.Clamp(minutes, MinRemoteRefreshMinutes, MaxRemoteRefreshMinutes);

    public const double MinThresholdPercent = 50;
    public const double MaxThresholdPercent = 100;

    public static double ClampThreshold(double percent) => Math.Clamp(percent, MinThresholdPercent, MaxThresholdPercent);

    // 20 is the floor: low enough to see straight through the window onto the desktop behind it,
    // while Maximum (100, also the default) keeps the window fully opaque - see ThemeService.Apply.
    public const int MinWindowOpacityPercent = 20;
    public const int MaxWindowOpacityPercent = 100;

    public static int ClampWindowOpacityPercent(int percent) => Math.Clamp(percent, MinWindowOpacityPercent, MaxWindowOpacityPercent);

    public const int MinAttentionMaxAgeMinutes = 5;
    public const int MaxAttentionMaxAgeMinutes = 1440; // 24 hours - the settings window's own top rung.

    public static int ClampAttentionMaxAgeMinutes(int minutes) => Math.Clamp(minutes, MinAttentionMaxAgeMinutes, MaxAttentionMaxAgeMinutes);

    // Same floor as the Stats window's own XAML MinWidth/MinHeight (see StatsWindow.xaml) - a
    // remembered size can never be smaller than the window itself would ever allow dragging it to.
    // No ceiling: ResolveStatsWindowSize already falls back to the default once a remembered size no
    // longer fits the monitor it would open on, the same "clamp on load, resolve against the
    // screen at open time" split SettingsStore.ClampWindow/WindowPlacementService.ResolvePosition
    // already use for the main window's own remembered placement.
    public const double MinStatsWindowWidth = 480;
    public const double MinStatsWindowHeight = 420;

    public static double ClampStatsWindowWidth(double width) =>
        double.IsFinite(width) ? Math.Max(MinStatsWindowWidth, width) : MinStatsWindowWidth;

    public static double ClampStatsWindowHeight(double height) =>
        double.IsFinite(height) ? Math.Max(MinStatsWindowHeight, height) : MinStatsWindowHeight;

    /// <summary>The threshold/enabled pair that actually applies to one provider's window: its own
    /// value when that provider's <see cref="ThresholdSettings.UseCustom"/> is set, otherwise
    /// <see cref="AppSettings.DefaultThreshold"/>/<see cref="AppSettings.DefaultThresholdEnabled"/> -
    /// the single decision point <see cref="ViewModels.MainViewModel.OnSnapshotReady"/> asks before
    /// calling <see cref="NotificationService.Evaluate"/>, so that method itself never needs to know
    /// where a number came from.</summary>
    public static (double Threshold, bool Enabled) ResolveThreshold(AppSettings settings, string providerId, WindowKind kind)
    {
        if (settings.Providers.TryGetValue(providerId, out var providerSettings) && providerSettings.Thresholds.UseCustom)
        {
            var t = providerSettings.Thresholds;
            return kind switch
            {
                WindowKind.FiveHour => (t.FiveHour, t.FiveHourEnabled),
                WindowKind.Weekly => (t.Weekly, t.WeeklyEnabled),
                _ => (t.Other, t.OtherEnabled),
            };
        }

        return (settings.DefaultThreshold, settings.DefaultThresholdEnabled);
    }

    /// <summary>The retention slider's rungs: days below a year, then every whole year from one to
    /// ten - no gaps (four, six, seven, eight and nine years used to be unreachable).</summary>
    public static readonly IReadOnlyList<int> RetentionRungsDays =
        [7, 14, 30, 60, 90, 180, 365, 730, 1095, 1460, 1825, 2190, 2555, 2920, 3285, 3650];

    /// <summary>Clamps to the rung range first, then snaps to the nearest rung - "6 -> 7" and
    /// "4000 -> 3650" both fall out of the clamp step alone; an interior value snaps to whichever
    /// rung is closest.</summary>
    public static int SnapRetentionDays(int days)
    {
        var clamped = Math.Clamp(days, RetentionRungsDays[0], RetentionRungsDays[^1]);
        return RetentionRungsDays.MinBy(rung => Math.Abs(rung - clamped));
    }

    /// <summary>"LabelKey" rather than a literal label - <see cref="ViewModels.SettingsViewModel"/>
    /// resolves it through <see cref="LocalizationService"/> at the point of use, so a language switch
    /// reaches these too.</summary>
    public sealed record ChartRangeOption(string Value, string LabelKey, int RequiredRetentionDays);

    private static readonly IReadOnlyList<ChartRangeOption> AllChartRanges =
    [
        new("Day", "Chart.Range.Day", 1),
        new("Week", "Chart.Range.Week", 7),
        new("Month", "Chart.Range.Month", 30),
        new("Year", "Chart.Range.Year", 365),
        // 0, not the longest retention rung: "All" means whatever history actually exists, so it
        // stays offered at every retention setting instead of only once retention is dragged to
        // its own maximum.
        new("All", "Chart.Range.All", 0),
    ];

    /// <summary>Only the ranges the current retention can actually cover are offered: a
    /// 30-day retention offers neither the one-year range nor the whole history.</summary>
    public static IReadOnlyList<ChartRangeOption> AvailableChartRanges(int retentionDays) =>
        AllChartRanges.Where(option => retentionDays >= option.RequiredRetentionDays).ToList();

    /// <summary>Turns a <see cref="Models.AppSettings.ChartRange"/> value into how far back
    /// <see cref="Storage.HistoryStore.Load"/> should read. "All" uses the longest retention rung
    /// rather than <see cref="TimeSpan.MaxValue"/> - history is never actually kept longer than that.</summary>
    public static TimeSpan ChartRangeToTimeSpan(string range) => range switch
    {
        "Day" => TimeSpan.FromDays(1),
        "Week" => TimeSpan.FromDays(7),
        "Month" => TimeSpan.FromDays(30),
        "Year" => TimeSpan.FromDays(365),
        _ => TimeSpan.FromDays(RetentionRungsDays[^1]),
    };
}
