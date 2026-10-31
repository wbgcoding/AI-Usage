using AiUsage.Models;
using AiUsage.Stats.Pricing;

namespace AiUsage.Stats;

/// <summary>One signed-in account of a provider: its key (<c>claude</c>, <c>claude#2</c>), its
/// normalized plan name when known, and its quota history.</summary>
public sealed record ForecastAccount(string Key, string? Plan, IReadOnlyList<HistoryPoint> Points);

/// <summary>What an average window period looked like: how many periods had any use, the tokens used
/// in one of them on average and the mean peak percent they reached.</summary>
public sealed record ForecastAverage(int PeriodCount, long AverageTokens, double AveragePeak);

/// <summary>One quota window of a provider: the capacity estimate, the API value of a full window and
/// the user's own average.</summary>
public sealed record ForecastWindow(
    PerPercentResult Result, double? ApiUsdPerPercent, IReadOnlyList<string> UnpricedModels, ForecastAverage? Average)
{
    /// <summary>The tokens that fill a whole window: a hundred times the tokens per percent. 0 without an estimate.</summary>
    public long CapacityTokens => Result.TotalPerPercent * 100;

    /// <summary>The API cost of the tokens that fill a whole window.</summary>
    public double? ApiUsdPerWindow => ApiUsdPerPercent * 100;
}

/// <summary>The forecast of one provider: both windows (null when the provider has no history for it),
/// the plans of its accounts and the monthly API value against the monthly plan price.</summary>
public sealed record ProviderForecast(
    string ProviderId, ForecastWindow? FiveHour, ForecastWindow? Weekly, IReadOnlyList<string> Plans, int AccountCount,
    double? MonthlyApiUsd, double? MonthlyPlanUsd)
{
    /// <summary>How many times the plan price the API value of a month is.</summary>
    public double? Ratio => MonthlyApiUsd is { } api && MonthlyPlanUsd is > 0 ? api / MonthlyPlanUsd : null;
}

/// <summary>
/// The plan forecast: what a full five-hour window and a full week of a provider's plan amount to in
/// tokens and in API cost, and how much of that the user really uses. Accounts of one provider are
/// pooled, as their usage is mixed in the local logs.
/// </summary>
public static class StatsPlanForecast
{
    private const double DaysPerMonth = 30.44;
    private const string FiveHourKey = "Window_FiveHour";
    private const string WeeklyKey = "Window_Weekly";

    public static ProviderForecast? Build(
        string providerId, IReadOnlyList<ForecastAccount> accounts, IReadOnlyList<StatsRecord> records,
        Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone)
    {
        var weekly = BuildWindow(WindowKind.Weekly, accounts, records, pricer, now, zone);
        var fiveHour = BuildWindow(WindowKind.FiveHour, accounts, records, pricer, now, zone);
        if (weekly is null && fiveHour is null)
            return null;

        var plans = accounts.Select(account => account.Plan).Where(plan => !string.IsNullOrWhiteSpace(plan))
            .Select(plan => plan!).Distinct().ToList();
        var prices = accounts.Select(account => PlanPriceList.MonthlyUsd(providerId, account.Plan)).ToList();
        double? planUsd = accounts.Count > 0 && prices.All(price => price is not null) ? prices.Sum(price => price!.Value) : null;
        double? monthlyApi = weekly?.ApiUsdPerWindow * DaysPerMonth / 7 * accounts.Count;
        return new ProviderForecast(providerId, fiveHour, weekly, plans, accounts.Count, monthlyApi, planUsd);
    }

    private static ForecastWindow? BuildWindow(
        WindowKind kind, IReadOnlyList<ForecastAccount> accounts, IReadOnlyList<StatsRecord> records,
        Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone)
    {
        var lists = accounts.Select(account => PointsOf(account.Points, kind)).Where(points => points.Count > 0).ToList();
        if (lists.Count == 0)
            return null;

        var intervals = StatsTokensPerPercent.BuildPooledIntervals(records, lists, zone, pricer);
        var result = StatsTokensPerPercent.Estimate(intervals);
        var average = AveragePeriod(lists, kind, records, now.AddDays(-StatsTokensPerPercent.LookbackDays), now, zone);
        if (result.Status == PerPercentStatus.TooFew)
            return new ForecastWindow(result, null, [], average);

        // Hours whose models all lack a price say nothing about the cost, so they drop out of both sides.
        var priced = intervals.Where(interval => interval.AnyPriced).ToList();
        var percent = priced.Sum(interval => interval.DeltaPercent);
        double? perPercent = priced.Count > 0 && percent > 0 ? priced.Sum(interval => interval.CostUsd) / percent : null;
        var unpriced = intervals.Where(interval => interval.UnpricedModels is not null)
            .SelectMany(interval => interval.UnpricedModels!).Distinct().Order(StringComparer.Ordinal).ToList();
        return new ForecastWindow(result, perPercent, unpriced, average);
    }

    /// <summary>The generic window of a kind: points with their own label (a model-specific window) are not it.</summary>
    private static List<HistoryPoint> PointsOf(IEnumerable<HistoryPoint> points, WindowKind kind)
    {
        var key = kind == WindowKind.FiveHour ? FiveHourKey : WeeklyKey;
        return points.Where(point => point.Window == kind && StatsLimitsAggregator.LabelKeyOf(point) == key).ToList();
    }

    /// <summary>
    /// The user's average window: over the periods that ended inside the lookback and saw any use (a
    /// peak above 0), the tokens used during one period and the mean peak percent. The tokens come from
    /// the hours with a reading pair (tokens per percent there, times the percent the periods reached),
    /// each hour once even when pooled accounts' periods overlap. Null when no period qualifies or no
    /// hour of them has a reading pair.
    /// </summary>
    public static ForecastAverage? AveragePeriod(
        IReadOnlyList<IReadOnlyList<HistoryPoint>> accountsPoints, WindowKind kind, IReadOnlyList<StatsRecord> records,
        DateTimeOffset lookbackStart, DateTimeOffset now, TimeZoneInfo zone)
    {
        var length = kind == WindowKind.FiveHour ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var peaks = new List<double>();
        foreach (var points in accountsPoints)
        {
            foreach (var period in StatsLimitsAggregator.SplitIntoPeriods(PointsOf(points, kind)))
            {
                if (period[0].ResetsAt is null)
                    continue;

                var end = period.Max(point => point.ResetsAt!.Value);
                var start = end - length;
                var peak = period.Max(point => point.Percent);
                if (end > now || start < lookbackStart || !(peak > 0))
                    continue;

                ranges.Add((start, end));
                peaks.Add(peak);
            }
        }

        if (peaks.Count == 0)
            return null;

        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, range.End > merged[^1].End ? range.End : merged[^1].End);
            else
                merged.Add(range);
        }

        var inRanges = new List<StatsRecord>();
        foreach (var record in records)
        {
            if (record.Hour is < 0 or > 23)
                continue;

            var middle = record.Day.ToDateTime(new TimeOnly(record.Hour, 30));
            var moment = new DateTimeOffset(middle, zone.GetUtcOffset(middle));
            if (merged.Any(range => moment >= range.Start && moment < range.End))
                inRanges.Add(record);
        }

        // Only hours with a reading pair at both ends tell how many tokens a percent cost, so the tokens
        // of the periods are estimated from those hours (the same ones the capacity is fitted on) and
        // scaled to the percent the periods really reached. Counting every logged token instead would
        // include hours the limit was never read for, and the average could exceed the capacity.
        var covered = StatsTokensPerPercent.BuildPooledIntervals(inRanges, accountsPoints, zone, null);
        var coveredPercent = covered.Sum(interval => interval.DeltaPercent);
        var coveredTokens = covered.Sum(interval => interval.TokensByModel.Values.Sum());
        if (!(coveredPercent > 0) || coveredTokens <= 0)
            return null;

        var tokens = coveredTokens / coveredPercent * peaks.Sum();
        return new ForecastAverage(peaks.Count, (long)(tokens / peaks.Count + 0.5), peaks.Average());
    }
}
