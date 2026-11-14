using AiUsage.Models;

namespace AiUsage.Stats;

/// <summary>One quota window of one provider, summed up over the shown range: how often its limit was
/// hit, the highest value it reached and the mean of the per-period peaks. The label is the resource
/// key (or a plain name) the view resolves; this record holds numbers only. The readings span from
/// <see cref="FirstReading"/> to <see cref="LastReading"/>; <see cref="LastReachedAt"/> is the newest
/// reading at the limit (null when it never was) and <see cref="BlockedTime"/> the time the window stayed
/// at the limit, summed over its periods. <see cref="EarlyResetCount"/> counts the limit resets used in
/// the range (a cycle cleared before its regular reset) and <see cref="LastEarlyResetAt"/> is the newest.</summary>
public sealed record StatsLimitRow(
    string ProviderId, WindowKind Window, string WindowLabelKey, int PeriodCount, int ReachedCount, double Highest, double AveragePeak,
    DateTimeOffset FirstReading = default, DateTimeOffset LastReading = default, DateTimeOffset? LastReachedAt = null,
    TimeSpan BlockedTime = default, int EarlyResetCount = 0, DateTimeOffset? LastEarlyResetAt = null);

/// <summary>
/// Turns the stored quota history into the rows of the statistics window's limits section. A period
/// is one cycle of a window as <see cref="CycleBoundaries"/> cuts it (a regular reset, a limit reset used
/// before it, or a plan change starts a new one), so "limit reached" counts windows, not individual
/// readings. Pure: no clock, no file access.
/// </summary>
public static class StatsLimitsAggregator
{
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
                var cycles = SplitIntoCycles(group);
                var periods = cycles.Select(cycle => cycle.Points).ToList();
                var peaks = periods.Select(period => period.Max(point => point.Percent)).ToList();
                var earlyResets = cycles.Where(cycle => cycle.StartedByEarlyReset).Select(cycle => cycle.Points[0].Timestamp).ToList();
                var reached = group.Where(point => point.Percent >= LimitPercent).Select(point => (DateTimeOffset?)point.Timestamp).Max();
                rows.Add(new StatsLimitRow(
                    providerId, group.Key.Window, group.Key.Key, periods.Count,
                    peaks.Count(peak => peak >= LimitPercent), peaks.Max(), peaks.Average(),
                    group.Min(point => point.Timestamp), group.Max(point => point.Timestamp), reached,
                    BlockedTimeOf(group.Key.Window, cycles), earlyResets.Count, earlyResets.Count > 0 ? earlyResets.Max() : null));
            }
        }

        return rows;
    }

    /// <summary>How long a window stayed blocked: per period the time from its first reading at the limit to its end (its
    /// reset, or the moment a limit reset or plan change cut it short), never below zero and, for the five-hour and weekly
    /// window, never above the window's own length (a reset time that wobbles or lies far ahead must not claim more). A
    /// period without a reset time counts for nothing.</summary>
    private static TimeSpan BlockedTimeOf(WindowKind window, List<Cycle> cycles)
    {
        var total = TimeSpan.Zero;
        foreach (var cycle in cycles)
        {
            var period = cycle.Points;
            if (period.Where(point => point.Percent >= LimitPercent).Select(point => (DateTimeOffset?)point.Timestamp).Min() is not { } hit
                || period.Max(point => point.ResetsAt) is not { } reset)
                continue;

            var end = cycle.CutAt is { } cut && cut < reset ? cut : reset;
            var blocked = end - hit;
            var longest = window switch
            {
                WindowKind.FiveHour => TimeSpan.FromHours(5),
                WindowKind.Weekly => TimeSpan.FromDays(7),
                _ => TimeSpan.MaxValue,
            };
            total += blocked < TimeSpan.Zero ? TimeSpan.Zero : blocked > longest ? longest : blocked;
        }

        return total;
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

    /// <summary>One cycle of a window: its readings, the moment it was cut short (the first reading of the next
    /// cycle after a limit reset or plan change; null when it ran to its regular reset or is the last) and whether a
    /// limit reset started it.</summary>
    internal sealed record Cycle(List<HistoryPoint> Points, DateTimeOffset? CutAt, bool StartedByEarlyReset);

    internal static List<List<HistoryPoint>> SplitIntoPeriods(IEnumerable<HistoryPoint> points) =>
        SplitIntoCycles(points).Select(cycle => cycle.Points).ToList();

    /// <summary>Cuts the readings of one window into cycles at every boundary <see cref="CycleBoundaries"/> finds, in
    /// time order. A window that never reports a reset time cannot be cut: all its readings are one cycle, last.</summary>
    internal static List<Cycle> SplitIntoCycles(IEnumerable<HistoryPoint> points)
    {
        var cycles = new List<Cycle>();
        var all = points.ToList();
        var timed = all.Where(point => point.ResetsAt is not null).OrderBy(point => point.Timestamp).ToList();
        List<HistoryPoint>? current = null;
        for (var i = 0; i < timed.Count; i++)
        {
            var kind = i == 0 ? CycleBoundary.None : CycleBoundaries.Classify(timed[i - 1], timed[i]);
            if (current is null || kind != CycleBoundary.None)
            {
                if (kind is CycleBoundary.EarlyReset or CycleBoundary.PlanChange)
                    cycles[^1] = cycles[^1] with { CutAt = timed[i].Timestamp };
                current = [];
                // The first reading of the range has nothing before it, so only its stored flag can tell.
                cycles.Add(new Cycle(current, null, kind == CycleBoundary.EarlyReset || (i == 0 && timed[i].EarlyReset == true)));
            }

            current.Add(timed[i]);
        }

        var withoutReset = all.Where(point => point.ResetsAt is null).ToList();
        if (withoutReset.Count > 0)
            cycles.Add(new Cycle(withoutReset, null, false));
        return cycles;
    }
}
