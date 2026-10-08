using System.Text.RegularExpressions;
using System.Globalization;
using System.Windows.Media;

namespace AiUsage.Stats;

/// <summary>Which axis the statistics window's chart and table are currently grouped by.</summary>
public enum StatsGrouping
{
    Day,
    Week,
    Model,
    Project,
    Effort,
}

/// <summary>One row of a grouped result: a label (a day, an ISO week, a model or a project name)
/// and its token total, broken into stacked segments for the chart - one segment per provider for
/// day/week grouping, a single segment for model/project grouping (a model or project already
/// belongs to exactly one provider, so stacking would add nothing there).</summary>
public readonly record struct StatsGroupedRow(string Label, IReadOnlyList<long> StackedValues, long Total);

/// <summary>The token totals for one period, split into the input/output/cache breakdown line and
/// the headline total, plus the same total for the immediately preceding period of equal length -
/// what the change-vs-previous-period figure is computed from.</summary>
public readonly record struct StatsPeriodSummary(long Total, long PreviousTotal, long InputTokens, long OutputTokens, long CacheTokens);

/// <summary>The figures bar's own five cards: the period total, its average per calendar
/// day, the single busiest day with its own date and total, the signed percentage change
/// against the immediately preceding period of equal length, and <see cref="ActiveDayCount"/> - the
/// number of distinct days in the period that actually saw any tokens, which the view divides the
/// total by for its "average per active day" card. <see cref="HasPreviousPeriod"/> is false whenever
/// that preceding period holds no records at all (an empty history, or the "All" range, which
/// defines no such period to begin with) - the view leaves the change figure out entirely then,
/// rather than showing a misleading "0%".</summary>
public readonly record struct StatsHeadlineFigures(
    long Total, double PerDayAverage, DateOnly? BusiestDay, long BusiestDayTotal, bool HasPreviousPeriod, double ChangePercent,
    int ActiveDayCount);

/// <summary>One slice of a donut chart: a label (a provider or a model display name, or the
/// pooled "Other"/"Andere" entry), its own token total and its percentage of the whole - the
/// percentage a caller only ever needs for the legend text, since <see
/// cref="Views.Controls.StatsRingChart.SweepAngles"/> recomputes the actual sweep angles itself from
/// the same percentages. <see cref="ProviderId"/> is only ever filled in by <see
/// cref="ShareByModel"/>, which is the one caller that can name a single provider a model belongs
/// to - the provider ring's own slices are already one provider each, and the pooled entry belongs
/// to none. <see cref="Hour"/> is only filled in by <see cref="DayDetail"/>'s hour list, so a
/// caller can put the bars back in clock order whatever the label looks like.</summary>
public readonly record struct StatsShareSlice(string Label, long Total, double Percent, string ProviderId = "", int Hour = -1);

/// <summary>One row of the "top projects" panel: <see cref="ShortLabel"/> is what the bar draws (the
/// project's own last folder segment, further trimmed if still long), <see cref="FullPath"/> is the
/// untrimmed value the tooltip names it by. <see cref="FirstActivity"/>/<see cref="LastActivity"/>
/// are this project's own earliest and latest day within whatever period the caller passed in, not
/// the project's lifetime - the same period-scoping every other panel on this window already uses.
/// <see cref="InputTokens"/>/<see cref="OutputTokens"/>/<see cref="CachedTokens"/> (cache write plus
/// cache read combined into one figure) and <see cref="ProviderIds"/>/<see cref="MainModel"/> (the
/// model with this project's own largest total) exist only for the detailed hover tooltip <see
/// cref="Views.Controls.StatsHorizontalBarChart"/> builds for the "top projects" panel - every other
/// caller of this same row shape (the day-detail panel's own model/project breakdown) leaves them at
/// their defaults and never reads them back. <see cref="Color"/>/<see cref="IconPath"/> are filled in
/// after the fact, by <see cref="StatsViewModel"/> merging in <see cref="ProjectColorResolver"/>'s own
/// result - never by this aggregator itself, which has no folder to read an icon from and stays pure.
/// </summary>
public readonly record struct StatsProjectRow(
    string ShortLabel, string FullPath, long Total, double Percent, DateOnly FirstActivity, DateOnly LastActivity,
    long InputTokens = 0, long OutputTokens = 0, long CachedTokens = 0,
    IReadOnlyList<string>? ProviderIds = null, string MainModel = "",
    Color Color = default, string? IconPath = null,
    // Null forever in practice today: nothing this app reads counts individual sessions per
    // project. Kept as its own nullable field, not just left off, so the hover tooltip's own
    // "Sessions: {0}" line already has somewhere to read a real count from the day this app (or a
    // test standing in for it) gains one, without another StatsProjectRow field ever needing to
    // change again.
    int? SessionCount = null);

/// <summary>The month grid's own day-detail panel: one day's usage broken four ways, every list
/// already sorted by total, descending. <see cref="ByHour"/> is the one exception to "only entries
/// that actually occurred" - once the day has any record at all it always holds all 24 hours (a
/// silent hour sums to zero), so the panel can show a complete day rather than an arbitrary subset;
/// a day with no records at all leaves it empty too, the same as the other three.</summary>
public readonly record struct StatsDayDetail(
    IReadOnlyList<StatsShareSlice> ByProvider,
    IReadOnlyList<StatsShareSlice> ByModel,
    IReadOnlyList<StatsShareSlice> ByProject,
    IReadOnlyList<StatsShareSlice> ByHour)
{
    public static readonly StatsDayDetail Empty = new([], [], [], []);
}

/// <summary>The cache-share bar's own four segments, in the fixed order the bar itself draws
/// them: new (non-cached) input, cache write, cache read, output - the same four token kinds <see
/// cref="StatsRecord"/> already keeps apart, just summed over a period instead of broken out per
/// day/model/project.</summary>
public readonly record struct StatsCacheShareRow(long NewInput, long CacheWrite, long CacheRead, long Output)
{
    public long Total => NewInput + CacheWrite + CacheRead + Output;

    public IReadOnlyList<long> AsValues => [NewInput, CacheWrite, CacheRead, Output];
}

/// <summary>
/// Pure grouping and summarising logic over an already-loaded set of <see cref="StatsRecord"/>
/// rows - no store, no clock, so every case is a plain, deterministic function of its inputs.
/// <see cref="StatsViewModel"/> is the only caller in production; everything here is exercised
/// directly by fixture data instead.
/// </summary>
public static class StatsAggregator
{
    /// <summary>Fixed stacking order for day/week grouping - the only two providers a
    /// <see cref="StatsRecord"/> can ever name, always in the same order so a bar's colours
    /// never swap between two consecutive periods.</summary>
    public static readonly IReadOnlyList<string> StackedProviderOrder = [StatsIndexer.ClaudeProviderId, StatsIndexer.CodexProviderId];

    /// <summary><paramref name="rangeStart"/>/<paramref name="rangeEnd"/> only ever affect Day and Week
    /// grouping: every calendar day (or ISO week) across that span gets a row, even one with no records at
    /// all, so a gap in the underlying data draws as an empty slot rather than vanishing from the
    /// axis. Left null (the default), Day grouping falls back to its old behaviour - one row per day
    /// that actually has records, nothing filled in. <paramref name="noProjectLabel"/> names the
    /// Project grouping's row for records with no project at all, the same pooling <see
    /// cref="TopProjects"/> does.</summary>
    public static IReadOnlyList<StatsGroupedRow> Group(
        IReadOnlyList<StatsRecord> records, StatsGrouping grouping, DateOnly? rangeStart = null, DateOnly? rangeEnd = null,
        string noProjectLabel = "") => grouping switch
    {
        StatsGrouping.Day => GroupByDay(records, rangeStart, rangeEnd),
        StatsGrouping.Week => GroupByWeek(records, rangeStart, rangeEnd),
        StatsGrouping.Model => GroupBySingleValueKey(records, record => record.Model),
        StatsGrouping.Project => GroupBySingleValueKey(
            records, record => string.IsNullOrEmpty(record.Project) ? noProjectLabel : ProjectKey(record.Project), StringComparer.OrdinalIgnoreCase),
        StatsGrouping.Effort => GroupBySingleValueKey(records, record => record.Effort),
        _ => [],
    };

    private static List<StatsGroupedRow> GroupByDay(IReadOnlyList<StatsRecord> records, DateOnly? rangeStart, DateOnly? rangeEnd)
    {
        var byDay = records.GroupBy(record => record.Day).ToDictionary(
            group => group.Key, group => ToStackedRow(group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), group));

        if (rangeStart is not { } start || rangeEnd is not { } end || end < start)
            return byDay.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList();

        var zeroStack = StackedProviderOrder.Select(_ => 0L).ToList();
        var rows = new List<StatsGroupedRow>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var label = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            rows.Add(byDay.TryGetValue(day, out var row) ? row : new StatsGroupedRow(label, zeroStack, 0));
        }
        return rows;
    }

    private static List<StatsGroupedRow> GroupByWeek(IReadOnlyList<StatsRecord> records, DateOnly? rangeStart, DateOnly? rangeEnd)
    {
        var byWeek = records.GroupBy(record => IsoWeekLabel(record.Day)).ToDictionary(group => group.Key, group => ToStackedRow(group.Key, group));

        if (rangeStart is not { } start || rangeEnd is not { } end || end < start)
            return byWeek.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value).ToList();

        var zeroStack = StackedProviderOrder.Select(_ => 0L).ToList();
        var rows = new List<StatsGroupedRow>();
        var monday = WeekStart(start);
        for (; monday <= end; monday = monday.AddDays(7))
        {
            var label = IsoWeekLabel(monday);
            rows.Add(byWeek.TryGetValue(label, out var row) ? row : new StatsGroupedRow(label, zeroStack, 0));
        }
        return rows;
    }

    /// <summary>The Monday of the ISO week <paramref name="day"/> falls in - the one week start the
    /// whole app uses (week tokens, the day grid, the stats week buckets), whatever the culture's own
    /// calendar says. Walks back, never forward; the first calendar day has no earlier Monday and stays
    /// at <see cref="DateOnly.MinValue"/>.</summary>
    internal static DateOnly WeekStart(DateOnly day)
    {
        var back = ((int)day.DayOfWeek + 6) % 7;
        return day.DayNumber < back ? DateOnly.MinValue : day.AddDays(-back);
    }

    /// <summary>ISO 8601 week label ("2026-W01") - <see cref="ISOWeek"/> already resolves the year a
    /// week belongs to correctly across the turn of the calendar year (the last days of December can
    /// belong to week 1 of the next ISO year, and the first days of January can belong to the last
    /// week of the previous one), so no boundary case needs handling by hand here.</summary>
    internal static string IsoWeekLabel(DateOnly day)
    {
        var dateTime = day.ToDateTime(TimeOnly.MinValue);
        var isoYear = System.Globalization.ISOWeek.GetYear(dateTime);
        var isoWeek = System.Globalization.ISOWeek.GetWeekOfYear(dateTime);
        return $"{isoYear}-W{isoWeek:00}";
    }

    private static StatsGroupedRow ToStackedRow(string label, IEnumerable<StatsRecord> group)
    {
        var byProvider = group.GroupBy(record => record.Provider).ToDictionary(g => g.Key, g => g.Sum(record => record.TotalTokens));
        var stacked = StackedProviderOrder.Select(provider => byProvider.GetValueOrDefault(provider)).ToList();
        return new StatsGroupedRow(label, stacked, stacked.Sum());
    }

    private static List<StatsGroupedRow> GroupBySingleValueKey(IReadOnlyList<StatsRecord> records, Func<StatsRecord, string> keySelector, IEqualityComparer<string>? comparer = null) =>
        records
            .GroupBy(keySelector, comparer)
            .Select(group =>
            {
                var total = group.Sum(record => record.TotalTokens);
                return new StatsGroupedRow(group.Key, [total], total);
            })
            .OrderByDescending(row => row.Total)
            .ToList();

    /// <summary>Never throws or divides by zero on an empty period - a period with no records at
    /// all summarises to every field at zero, and <see cref="Group"/> above already answers an
    /// empty list the same way for an empty input.</summary>
    public static StatsPeriodSummary Summarize(IReadOnlyList<StatsRecord> currentPeriod, IReadOnlyList<StatsRecord> previousPeriod) => new(
        Total: currentPeriod.Sum(record => record.TotalTokens),
        PreviousTotal: previousPeriod.Sum(record => record.TotalTokens),
        InputTokens: currentPeriod.Sum(record => record.InputTokens),
        OutputTokens: currentPeriod.Sum(record => record.OutputTokens),
        CacheTokens: currentPeriod.Sum(record => record.CacheCreationTokens + record.CacheReadTokens));

    /// <summary>The figures bar's five cards. <paramref name="periodDayCount"/> is the
    /// caller's own calendar-day length of <paramref name="currentPeriod"/>'s range (7 for a week,
    /// 30 for a month, and so on - "All" has no fixed length, so <see cref="StatsViewModel"/> derives
    /// one from the earliest record instead); zero or negative floors to one, so an empty period
    /// never divides by zero. <see cref="StatsHeadlineFigures.ActiveDayCount"/> counts only days with
    /// at least one token, unlike <paramref name="periodDayCount"/> which counts every calendar day
    /// in the range whether it saw any usage or not. <paramref name="previousPeriodDays"/> is the fixed
    /// calendar length of the preceding period: the change against it is only shown when at least a
    /// quarter of those days saw tokens, since a mostly empty period makes any percentage
    /// meaningless. Zero (the default) asks for no such minimum.</summary>
    public static StatsHeadlineFigures ComputeHeadlineFigures(
        IReadOnlyList<StatsRecord> currentPeriod, IReadOnlyList<StatsRecord> previousPeriod, int periodDayCount,
        int previousPeriodDays = 0)
    {
        var total = currentPeriod.Sum(record => record.TotalTokens);
        var perDayAverage = (double)total / Math.Max(1, periodDayCount);

        DateOnly? busiestDay = null;
        var busiestDayTotal = 0L;
        var activeDayCount = 0;
        foreach (var group in currentPeriod.GroupBy(record => record.Day))
        {
            var dayTotal = group.Sum(record => record.TotalTokens);
            if (dayTotal > 0)
                activeDayCount++;
            if (busiestDay is null || dayTotal > busiestDayTotal)
            {
                busiestDay = group.Key;
                busiestDayTotal = dayTotal;
            }
        }

        var previousActiveDays = previousPeriod.GroupBy(record => record.Day).Count(group => group.Sum(record => record.TotalTokens) > 0);
        var hasPreviousPeriod = previousPeriod.Count > 0 && previousActiveDays >= (int)Math.Ceiling(previousPeriodDays / 4.0);
        var previousTotal = previousPeriod.Sum(record => record.TotalTokens);
        var changePercent = hasPreviousPeriod && previousTotal > 0 ? (total - previousTotal) * 100.0 / previousTotal : 0;

        return new StatsHeadlineFigures(total, perDayAverage, busiestDay, busiestDayTotal, hasPreviousPeriod, changePercent, activeDayCount);
    }

    /// <summary>One entry per calendar day of the period, chronological, 0 for a day with no usage -
    /// the shared series every figures-bar mini chart (<see cref="Views.Controls.StatsFigureMiniChart"/>)
    /// draws its own shape from, so a sparkline, a set of columns and the single busiest column
    /// highlighted all agree on the exact same days. Always exactly <paramref name="periodDayCount"/>
    /// entries long (floored to one, the same floor <see cref="ComputeHeadlineFigures"/> already
    /// applies), even for a period whose earliest record does not reach all the way back to
    /// <paramref name="from"/> - those leading days are simply zero, not missing.</summary>
    public static IReadOnlyList<long> DailyTotalsSeries(IReadOnlyList<StatsRecord> currentPeriod, DateOnly from, int periodDayCount)
    {
        var days = Math.Max(1, periodDayCount);
        var totals = new long[days];
        foreach (var record in currentPeriod)
        {
            var offset = record.Day.DayNumber - from.DayNumber;
            if (offset >= 0 && offset < days)
                totals[offset] += record.TotalTokens;
        }
        return totals;
    }

    /// <summary>The index of the largest entry in <paramref name="values"/> - the one column <see
    /// cref="Views.Controls.StatsFigureMiniChartKind.Columns"/> draws in its own accent color instead
    /// of the shared muted one. The first entry wins a tie; an empty series has no busiest index at
    /// all.</summary>
    public static int? BusiestIndex(IReadOnlyList<long> values)
    {
        if (values.Count == 0)
            return null;

        var bestIndex = 0;
        for (var i = 1; i < values.Count; i++)
            if (values[i] > values[bestIndex])
                bestIndex = i;
        return bestIndex;
    }

    /// <summary>Shortens a token count the way the figures and the breakdown want it: three
    /// significant digits, zeros kept ("1.82 M", "33.5 M", "845 K"), with the caller's own magnitude
    /// word for thousands, millions and billions, and the plain number below one thousand. A value
    /// that rounds up to the next unit's one ("999.9 K") moves up into that unit. The suffix text
    /// itself is never hardcoded here - it comes from the caller (resource-driven, like every other
    /// piece of shown text in this app) so this stays a pure, language-agnostic function of its
    /// inputs.</summary>
    public static string ShortenTokenCount(long value, string thousandSuffix, string millionSuffix, string billionSuffix)
    {
        var magnitude = Math.Abs(value);
        if (magnitude < 1_000)
            return value.ToString("N0", CultureInfo.CurrentCulture);

        var units = new (double Divisor, string Suffix)[]
        {
            (1_000.0, thousandSuffix), (1_000_000.0, millionSuffix), (1_000_000_000.0, billionSuffix),
        };
        var index = magnitude >= 1_000_000_000 ? 2 : magnitude >= 1_000_000 ? 1 : 0;
        var (figure, decimals) = ThreeSignificant(value / units[index].Divisor);
        if (Math.Abs(figure) >= 1_000 && index < units.Length - 1)
        {
            index++;
            (figure, decimals) = ThreeSignificant(value / units[index].Divisor);
        }
        return $"{figure.ToString(decimals == 0 ? "0" : decimals == 1 ? "0.0" : "0.00", CultureInfo.CurrentCulture)} {units[index].Suffix}";
    }

    /// <summary>The Y axis' own shortening: at most one decimal ("20 M", "1.5 M") and a plain
    /// thousands-separated number below one million. Kept apart from <see
    /// cref="ShortenTokenCount"/> because a gridline label like "20.0 M" would only add noise to a
    /// round axis step.</summary>
    public static string ShortenTokenCountAxis(long value, string millionSuffix, string billionSuffix)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000_000)
            return $"{(value / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)} {billionSuffix}";
        if (magnitude >= 1_000_000)
            return $"{(value / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)} {millionSuffix}";
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>Rounds a scaled value to three significant digits: no decimals from 100 up, one from
    /// 10 up, two below. A value that rounds into the next band (9.996 to 10.0) takes that band's
    /// digits.</summary>
    private static (double Figure, int Decimals) ThreeSignificant(double scaled)
    {
        var decimals = Math.Abs(scaled) >= 100 ? 0 : Math.Abs(scaled) >= 10 ? 1 : 2;
        // Half-up on the magnitude by hand: the codebase's one rounding helper is built for usage
        // percentages, not for a scaled token count.
        var factor = decimals == 0 ? 1.0 : decimals == 1 ? 10.0 : 100.0;
        var rounded = Math.Sign(scaled) * Math.Floor(Math.Abs(scaled) * factor + 0.5) / factor;
        if (decimals == 2 && Math.Abs(rounded) >= 10)
            decimals = 1;
        else if (decimals == 1 && Math.Abs(rounded) >= 100)
            decimals = 0;
        return (rounded, decimals);
    }

    /// <summary>Shortens a token count for the donut charts' own center text: unlike <see
    /// cref="ShortenTokenCount"/>, a value below one million still gets a magnitude word once it
    /// reaches a thousand - the ring's hole is narrow enough that a bare six-digit number would
    /// force the font down further than a "12 Tsd"/"12K" shorthand needs to. Every suffix already
    /// carries its own locale-correct spacing (DE leads with a space, EN does not, matching how "12
    /// Tsd" and "12K" are actually written), so this never inserts a separator of its own.</summary>
    public static string ShortenTokenCountCompact(long value, string thousandSuffix, string millionSuffix, string billionSuffix)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000_000)
            return $"{(value / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)}{billionSuffix}";
        if (magnitude >= 1_000_000)
            return $"{(value / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)}{millionSuffix}";
        if (magnitude >= 1_000)
            return $"{(value / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture)}{thousandSuffix}";
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>The "share per provider" ring - only the providers whose token usage this machine
    /// can count locally (<see cref="ProviderCoverage.HasLocalTokenData"/>), including one that saw no
    /// tokens this period, so it stays visible which counted providers were idle. Sorted by share,
    /// descending; a counted provider with a zero total sorts after every provider that has
    /// something, kept in <see cref="ProviderCoverage.AllProviderIds"/>'s own order by the sort's own
    /// stability.</summary>
    public static IReadOnlyList<StatsShareSlice> ShareByProvider(IReadOnlyList<StatsRecord> records)
    {
        var grandTotal = records.Sum(record => record.TotalTokens);
        var byProvider = records.GroupBy(record => record.Provider).ToDictionary(g => g.Key, g => g.Sum(record => record.TotalTokens));
        return ProviderCoverage.AllProviderIds
            .Where(ProviderCoverage.HasLocalTokenData)
            .Select(id => (Id: id, Total: byProvider.GetValueOrDefault(id)))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(entry.Id, entry.Total, grandTotal > 0 ? entry.Total * 100.0 / grandTotal : 0))
            .ToList();
    }

    /// <summary>The "share per effort level" ring: every level actually seen (including the empty
    /// "unknown" level, for a record whose own source line carried none), sorted by total, descending.
    /// Unlike <see cref="ShareByProvider"/> this never lists a level that saw no records at all - there
    /// is no fixed enum of every possible level the way <see cref="ProviderCoverage.AllProviderIds"/>
    /// is one for providers, so "every level" can only ever mean "every level this period actually
    /// used". The empty label is left exactly as it comes from <see cref="StatsRecord.Effort"/> -
    /// resolving it to a localized "Unknown" string is the view model's job, the same way <see
    /// cref="StatsViewModel"/> already resolves a raw model id before it ever reaches a bound
    /// property.</summary>
    public static IReadOnlyList<StatsShareSlice> ShareByEffort(IReadOnlyList<StatsRecord> records)
    {
        var grandTotal = records.Sum(record => record.TotalTokens);
        return records
            .GroupBy(record => record.Effort)
            .Select(group => (Label: group.Key, Total: group.Sum(record => record.TotalTokens)))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, grandTotal > 0 ? entry.Total * 100.0 / grandTotal : 0))
            .ToList();
    }

    /// <summary>The "share per model" ring: every model sorted by total, descending, with every
    /// model past the largest <paramref name="topCount"/> folded into one <paramref
    /// name="otherLabel"/> entry - never split further, and left out entirely when there is nothing
    /// left to pool. Each kept slice also names the provider it belongs to (<see
    /// cref="ChartPalette.ForModel"/> needs one to color it) - whichever provider contributed the
    /// larger sum, for the rare model name two providers both happen to use. The pooled entry names
    /// none, since it no longer belongs to any single provider.</summary>
    public static IReadOnlyList<StatsShareSlice> ShareByModel(IReadOnlyList<StatsRecord> records, int topCount, string otherLabel)
    {
        var grandTotal = records.Sum(record => record.TotalTokens);
        var byModel = records
            .GroupBy(record => record.Model)
            .Select(group => (
                Label: group.Key,
                Total: group.Sum(record => record.TotalTokens),
                ProviderId: group.GroupBy(record => record.Provider)
                    .OrderByDescending(providerGroup => providerGroup.Sum(record => record.TotalTokens))
                    .First().Key))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        double Percent(long total) => grandTotal > 0 ? total * 100.0 / grandTotal : 0;

        var slices = byModel.Take(topCount)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, Percent(entry.Total), entry.ProviderId))
            .ToList();
        var pooledTotal = byModel.Skip(topCount).Sum(entry => entry.Total);
        if (pooledTotal > 0)
            slices.Add(new StatsShareSlice(otherLabel, pooledTotal, Percent(pooledTotal)));
        return slices;
    }

    /// <summary>The cache-share bar: the same four token kinds <see cref="Summarize"/> already
    /// sums, just kept apart instead of merging cache write and cache read into one "cached"
    /// figure.</summary>
    public static StatsCacheShareRow CacheShare(IReadOnlyList<StatsRecord> records) => new(
        NewInput: records.Sum(record => record.InputTokens),
        CacheWrite: records.Sum(record => record.CacheCreationTokens),
        CacheRead: records.Sum(record => record.CacheReadTokens),
        Output: records.Sum(record => record.OutputTokens));

    /// <summary>The "top projects" panel: every project summed and sorted by total, descending,
    /// capped at <paramref name="limit"/> rows (the longest bar first, matching the panel's own draw
    /// order). A Codex rollout with no project name (an empty string) is pooled under
    /// <paramref name="noProjectLabel"/> rather than counted as its own, blank-labelled row - pooled
    /// the same way <see cref="ShareByModel"/> pools its own long tail, just keyed on emptiness
    /// instead of rank.</summary>
    public static IReadOnlyList<StatsShareSlice> TopProjects(IReadOnlyList<StatsRecord> records, int limit, string noProjectLabel)
    {
        var grandTotal = records.Sum(record => record.TotalTokens);
        return records
            .GroupBy(record => string.IsNullOrEmpty(record.Project) ? noProjectLabel : ProjectKey(record.Project), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Label: group.Key, Total: group.Sum(record => record.TotalTokens)))
            .OrderByDescending(entry => entry.Total)
            .Take(limit)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, grandTotal > 0 ? entry.Total * 100.0 / grandTotal : 0))
            .ToList();
    }

    /// <summary>How many characters a shortened project label is allowed before <see
    /// cref="MiddleEllipsis"/> trims it further - the control that draws the label still fits it to
    /// its own pixel width on top of this, so this only keeps an extreme folder name from dominating
    /// the tooltip build below rather than promising an exact fit.</summary>
    internal const int ProjectLabelMaxChars = 28;

    /// <summary>The same "top projects" panel as <see cref="TopProjects"/>, carrying the row's own
    /// full project path, share percentage and first/last activity day alongside its already-shortened
    /// display label - what the panel's tooltip needs beyond the bar chart's bare label and total.</summary>
    public static IReadOnlyList<StatsProjectRow> TopProjectsDetailed(IReadOnlyList<StatsRecord> records, int limit, string noProjectLabel)
    {
        var grandTotal = records.Sum(record => record.TotalTokens);
        return records
            .GroupBy(record => string.IsNullOrEmpty(record.Project) ? noProjectLabel : ProjectKey(record.Project), StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                FullPath: group.Key,
                Total: group.Sum(record => record.TotalTokens),
                First: group.Min(record => record.Day),
                Last: group.Max(record => record.Day),
                InputTokens: group.Sum(record => record.InputTokens),
                OutputTokens: group.Sum(record => record.OutputTokens),
                CachedTokens: group.Sum(record => record.CacheCreationTokens + record.CacheReadTokens),
                // Largest total first, in both cases - the tooltip only ever names the single
                // largest model, but every provider that touched this project at all, not just the
                // largest.
                ProviderIds: (IReadOnlyList<string>)[.. group
                    .GroupBy(record => record.Provider)
                    .OrderByDescending(providerGroup => providerGroup.Sum(record => record.TotalTokens))
                    .Select(providerGroup => providerGroup.Key)],
                MainModel: group
                    .GroupBy(record => record.Model)
                    .OrderByDescending(modelGroup => modelGroup.Sum(record => record.TotalTokens))
                    .Select(modelGroup => modelGroup.Key)
                    .FirstOrDefault(defaultValue: "")))
            .OrderByDescending(entry => entry.Total)
            .Take(limit)
            .Select(entry => new StatsProjectRow(
                MiddleEllipsis(ShortenProjectLabel(entry.FullPath), ProjectLabelMaxChars),
                entry.FullPath,
                entry.Total,
                grandTotal > 0 ? entry.Total * 100.0 / grandTotal : 0,
                entry.First,
                entry.Last,
                entry.InputTokens,
                entry.OutputTokens,
                entry.CachedTokens,
                entry.ProviderIds,
                entry.MainModel))
            .ToList();
    }

    private static readonly Regex AgentWorkFolder =
        new(@"^(?<root>.+?)[\\/](?:\.tmp|\.worktrees|\.claude[\\/]worktrees)(?:[\\/].*)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The key a project is grouped by. Agents work in throwaway folders below the project
    /// (<c>.tmp</c>, <c>.worktrees</c>, <c>.claude\worktrees</c>), so everything from the first such
    /// folder on is cut off and the work counts for the project itself. Trailing separators are
    /// trimmed so the same folder always yields the same key.</summary>
    internal static string ProjectKey(string project)
    {
        var match = AgentWorkFolder.Match(project);
        var key = (match.Success ? match.Groups["root"].Value : project).TrimEnd('\\', '/');
        return key.Length > 0 ? key : project;
    }

    /// <summary>Older Codex rows carry only a folder name ("Sample Tool") where Claude rows carry the
    /// full path. A bare name is replaced by the full project path whose last folder has the same name
    /// (case-insensitive) when exactly one such path exists among all records; otherwise it stays
    /// unchanged.</summary>
    public static IReadOnlyList<StatsRecord> ResolveBareProjectNames(IReadOnlyList<StatsRecord> records)
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!IsFullPath(record.Project))
                continue;
            var key = ProjectKey(record.Project);
            var name = ShortenProjectLabel(key);
            if (!byName.TryGetValue(name, out var paths))
                byName[name] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.Add(key);
        }

        var result = new List<StatsRecord>(records.Count);
        var changed = false;
        foreach (var record in records)
        {
            if (record.Project.Length > 0 && !IsFullPath(record.Project)
                && byName.TryGetValue(record.Project, out var paths) && paths.Count == 1)
            {
                result.Add(record with { Project = paths.First() });
                changed = true;
            }
            else
            {
                result.Add(record);
            }
        }

        return changed ? result : records;
    }

    private static bool IsFullPath(string project) => project.AsSpan().IndexOfAny('\\', '/') >= 0;

    /// <summary>A project's own display name, wherever this window shows one: the last folder segment
    /// of its full path (the part a person actually recognizes a project by), never the whole path -
    /// a Codex/Claude project key is a filesystem path in practice, so its own last segment is what a
    /// person would call that project. A value with no path separator at all (already a short label,
    /// such as the pooled "no project" entry) is returned unchanged. Trailing separators are trimmed
    /// first so a path that happens to end in one still yields its real last segment rather than an
    /// empty string.</summary>
    internal static string ShortenProjectLabel(string label)
    {
        var trimmed = label.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
            return label;

        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        return lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
    }

    /// <summary>Trims <paramref name="text"/> down to <paramref name="maxChars"/> by cutting out its
    /// own middle and splicing in "..." - unlike trimming the end, the part of a long name that most
    /// often tells one project apart from another (a distinguishing suffix) survives. Text already
    /// short enough passes through untouched, and a budget too small to fit even the ellipsis itself
    /// falls back to the plain end-trim a caller would get from any other short label.</summary>
    internal static string MiddleEllipsis(string text, int maxChars)
    {
        if (maxChars < 4 || text.Length <= maxChars)
            return text;

        var keep = maxChars - 3;
        var head = (keep + 1) / 2;
        var tail = keep - head;
        return string.Concat(text.AsSpan(0, head), "...", text.AsSpan(text.Length - tail));
    }

    /// <summary>The "by weekday" panel: seven columns, Monday..Sunday in every culture, the same
    /// week <see cref="WeekStart"/> gives the day grid and the week totals; only the day names come
    /// from <paramref name="culture"/>.</summary>
    public static IReadOnlyList<StatsGroupedRow> GroupByWeekday(IReadOnlyList<StatsRecord> records, CultureInfo culture)
    {
        var byWeekday = records.GroupBy(record => record.Day.DayOfWeek).ToDictionary(g => g.Key, g => g.Sum(record => record.TotalTokens));
        var dayNames = culture.DateTimeFormat.AbbreviatedDayNames;

        var rows = new List<StatsGroupedRow>(7);
        for (var offset = 0; offset < 7; offset++)
        {
            var day = (DayOfWeek)(((int)DayOfWeek.Monday + offset) % 7);
            var total = byWeekday.GetValueOrDefault(day);
            rows.Add(new StatsGroupedRow(dayNames[(int)day], [total], total));
        }
        return rows;
    }

    /// <summary>The "by hour" panel: 24 columns, hour 0..23 of <see cref="StatsRecord.Hour"/> -
    /// already in local time the same way <see cref="StatsRecord.Day"/> is, so the two stay consistent
    /// with each other. Each column's own label comes from <paramref name="culture"/>'s short time
    /// pattern, never a hardcoded "0..23" or "AM/PM" list.</summary>
    public static IReadOnlyList<StatsGroupedRow> GroupByHour(IReadOnlyList<StatsRecord> records, CultureInfo culture)
    {
        var byHour = records.GroupBy(record => record.Hour).ToDictionary(g => g.Key, g => g.Sum(record => record.TotalTokens));
        var rows = new List<StatsGroupedRow>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            var total = byHour.GetValueOrDefault(hour);
            var label = new DateTime(2000, 1, 1, hour, 0, 0).ToString("t", culture);
            rows.Add(new StatsGroupedRow(label, [total], total));
        }
        return rows;
    }

    /// <summary>The month grid's day-detail panel: <paramref name="day"/>'s own records broken down
    /// by provider, model (through <see cref="ModelDisplayNames.Resolve"/>), project and hour (labelled with <paramref name="culture"/>'s short time pattern, like <see cref="GroupByHour"/>), every
    /// list sorted by total, descending. A day with no matching records at all returns <see
    /// cref="StatsDayDetail.Empty"/> outright, rather than an hour list zero-filled for all 24 hours -
    /// there is nothing to show a breakdown of.</summary>
    public static StatsDayDetail DayDetail(IReadOnlyList<StatsRecord> records, DateOnly day, string noProjectLabel = "", CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var dayRecords = records.Where(record => record.Day == day).ToList();
        if (dayRecords.Count == 0)
            return StatsDayDetail.Empty;

        var dayTotal = dayRecords.Sum(record => record.TotalTokens);
        double Percent(long total) => dayTotal > 0 ? total * 100.0 / dayTotal : 0;

        var byProvider = dayRecords
            .GroupBy(record => record.Provider)
            .Select(group => (Label: group.Key, Total: group.Sum(record => record.TotalTokens)))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, Percent(entry.Total)))
            .ToList();

        // Each model carries the provider that used it most that day, the id its bar's color comes from.
        var byModel = dayRecords
            .GroupBy(record => record.Model)
            .Select(group => (
                Label: ModelDisplayNames.Resolve(group.Key),
                Total: group.Sum(record => record.TotalTokens),
                ProviderId: group.GroupBy(record => record.Provider)
                    .OrderByDescending(providerGroup => providerGroup.Sum(record => record.TotalTokens))
                    .First().Key))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, Percent(entry.Total), entry.ProviderId))
            .ToList();

        var byProject = dayRecords
            .GroupBy(record => string.IsNullOrEmpty(record.Project) ? noProjectLabel : ProjectKey(record.Project), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Label: group.Key, Total: group.Sum(record => record.TotalTokens)))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(entry.Label, entry.Total, Percent(entry.Total)))
            .ToList();

        var byHourTotals = dayRecords.GroupBy(record => record.Hour).ToDictionary(group => group.Key, group => group.Sum(record => record.TotalTokens));
        var byHour = Enumerable.Range(0, 24)
            .Select(hour => (Hour: hour, Total: byHourTotals.GetValueOrDefault(hour)))
            .OrderByDescending(entry => entry.Total)
            .Select(entry => new StatsShareSlice(
                new DateTime(2000, 1, 1, entry.Hour, 0, 0).ToString("t", culture), entry.Total, Percent(entry.Total), Hour: entry.Hour))
            .ToList();

        return new StatsDayDetail(byProvider, byModel, byProject, byHour);
    }
}
