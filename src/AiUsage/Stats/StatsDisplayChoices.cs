using System.Globalization;

namespace AiUsage.Stats;

/// <summary>What the "Token usage" page of Settings lets the user choose about how the statistics
/// window looks: the week start, the period it opens on and the sections it shows.</summary>
internal static class StatsDisplayChoices
{
    /// <summary>The setting value for "as in Windows".</summary>
    internal const string SystemWeekStart = "";

    /// <summary>The setting value for "the one it was left on".</summary>
    internal const string LastUsedRange = "";

    /// <summary>The periods the window can open on; Custom is not offered.</summary>
    internal static readonly string[] OpenRanges = ["Week", "Month", "ThisMonth", "LastMonth", "Year", "All"];

    internal static string NormalizeOpenRange(string? value) =>
        OpenRanges.FirstOrDefault(range => range == value) ?? LastUsedRange;

    internal static string NormalizeWeekStart(string? value) => value switch
    {
        "Monday" => "Monday",
        "Sunday" => "Sunday",
        _ => SystemWeekStart,
    };

    /// <summary>The first weekday of the weekday rows and the month grids; week totals stay Monday to Sunday.</summary>
    internal static DayOfWeek ResolveFirstDay(string? weekStart, CultureInfo culture) => NormalizeWeekStart(weekStart) switch
    {
        "Monday" => DayOfWeek.Monday,
        "Sunday" => DayOfWeek.Sunday,
        _ => culture.DateTimeFormat.FirstDayOfWeek,
    };

    /// <summary>The section keys of the window that can be switched off, with the resource key of each
    /// title, in the order of the default arrangement.</summary>
    internal static readonly IReadOnlyList<(string Key, string TitleKey)> Sections =
    [
        ("monthgrid", "Stats.MonthGrid.Head"),
        ("figures", "Stats.Section.Figures"),
        ("breakdown", "Stats.Section.Breakdown"),
        ("provider", "Stats.Chart.ByProvider"),
        ("model", "Stats.Chart.ByModel"),
        ("effort", "Stats.Chart.ByEffort"),
        ("planforecast", "Stats.Forecast"),
        ("limits", "Stats.Limits"),
        ("projects", "Stats.Chart.TopProjects"),
    ];

    /// <summary>The stored keys without unknown or repeated ones.</summary>
    internal static List<string> NormalizeHiddenSections(IEnumerable<string>? keys)
    {
        var known = Sections.Select(section => section.Key).ToHashSet();
        return [.. (keys ?? []).Where(key => key is not null && known.Contains(key)).Distinct()];
    }
}
