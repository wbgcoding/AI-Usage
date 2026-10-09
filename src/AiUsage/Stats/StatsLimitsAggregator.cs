using AiUsage.Models;

namespace AiUsage.Stats;

/// <summary>One quota window of one provider, summed up over the shown range: how often its limit was
/// hit, the highest value it reached and the mean of the per-period peaks. The label is the resource
/// key (or a plain name) the view resolves; this record holds numbers only.</summary>
public sealed record StatsLimitRow(
    string ProviderId, WindowKind Window, string WindowLabelKey, int PeriodCount, int ReachedCount, double Highest, double AveragePeak);

/// <summary>
/// Turns the stored quota history into the rows of the statistics window's limits section. A period
/// is a run of points that share one reset time (a window resets, a new period starts), so "limit
/// reached" counts windows, not individual readings. Pure: no clock, no file access.
/// </summary>
public static class StatsLimitsAggregator
{
    /// <summary>Reset times of one window that lie closer together than this are one period: a
    /// provider reports a reset as a time that can wobble by a few seconds between two reads.</summary>
    private static readonly TimeSpan SamePeriodGap = TimeSpan.FromMinutes(10);

    /// <summary>A reading at or above this counts as the limit being reached.</summary>
    internal const double LimitPercent = 100;

    /// <summary>One row per provider and window that has at least one point. Rows follow the order of
    /// <paramref name="series"/>, and inside a provider the five-hour, weekly, then other windows.</summary>
    public static IReadOnlyList<StatsLimitRow> Build(IEnumerable<(string ProviderId, IReadOnlyList<HistoryPoint> Points)> series)
    {
        var rows = new List<StatsLimitRow>();
        foreach (var (providerId, points) in series)
        {
            var groups = points
                .GroupBy(point => (point.Window, Key: LabelKeyOf(point)))
                .OrderBy(group => group.Key.Window)
                .ThenBy(group => group.Key.Key, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var periods = SplitIntoPeriods(group);
                var peaks = periods.Select(period => period.Max(point => point.Percent)).ToList();
                rows.Add(new StatsLimitRow(
                    providerId, group.Key.Window, group.Key.Key, periods.Count,
                    peaks.Count(peak => peak >= LimitPercent), peaks.Max(), peaks.Average()));
            }
        }

        return rows;
    }

    /// <summary>The resource key naming a point's window: its own label, or the generic one for its kind.</summary>
    internal static string LabelKeyOf(HistoryPoint point) =>
        point.Label is { Length: > 0 } label
            ? label
            : point.Window switch
            {
                WindowKind.FiveHour => "Window_FiveHour",
                WindowKind.Weekly => "Window_Weekly",
                _ => "Window_Other",
            };

    private static List<List<HistoryPoint>> SplitIntoPeriods(IEnumerable<HistoryPoint> points)
    {
        var periods = new List<List<HistoryPoint>>();
        var withoutReset = new List<HistoryPoint>();
        DateTimeOffset? lastReset = null;
        List<HistoryPoint>? current = null;
        foreach (var point in points.Where(point => point.ResetsAt is not null).OrderBy(point => point.ResetsAt))
        {
            if (current is null || point.ResetsAt!.Value - lastReset!.Value > SamePeriodGap)
            {
                current = [];
                periods.Add(current);
            }

            current.Add(point);
            lastReset = point.ResetsAt;
        }

        // A window that never reports a reset cannot be split: all its readings are one period.
        withoutReset.AddRange(points.Where(point => point.ResetsAt is null));
        if (withoutReset.Count > 0)
            periods.Add(withoutReset);
        return periods;
    }
}
