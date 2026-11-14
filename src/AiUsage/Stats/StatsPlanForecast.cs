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
/// the plans of its accounts and the monthly API value against the monthly plan price. With a plan
/// filter it covers that one plan only (<see cref="Plan"/>): <see cref="IsCurrent"/> tells the plan the
/// provider is on now from one it left, <see cref="LastReading"/> is its newest reading.</summary>
public sealed record ProviderForecast(
    string ProviderId, ForecastWindow? FiveHour, ForecastWindow? Weekly, IReadOnlyList<string> Plans, int AccountCount,
    double? MonthlyApiUsd, double? MonthlyPlanUsd, IReadOnlyList<PlanPrice>? PlanPrices = null,
    string? Plan = null, bool IsCurrent = true, DateTimeOffset? LastReading = null)
{
    /// <summary>The forecast's identity: provider and plan (<c>claude|Max</c>); the plan part is empty when unknown.</summary>
    public string Key => KeyOf(ProviderId, Plan);

    public static string KeyOf(string providerId, string? plan) => providerId + "|" + (plan ?? "");

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

    /// <summary>The plan an account is on now: the plan of its newest reading that has one, else the live one.</summary>
    public static string? CurrentPlanOf(ForecastAccount account) =>
        account.Points.Where(point => !string.IsNullOrWhiteSpace(point.Plan)).MaxBy(point => point.Timestamp)?.Plan ?? account.Plan;

    /// <summary>The plans an account has readings for in its history, its current one included. A reading without a
    /// plan counts for the account's current plan.</summary>
    public static IReadOnlyList<string?> PlansOf(ForecastAccount account)
    {
        var current = CurrentPlanOf(account);
        var plans = new List<string?> { current };
        foreach (var point in account.Points)
        {
            var plan = string.IsNullOrWhiteSpace(point.Plan) ? current : point.Plan;
            if (!plans.Contains(plan))
                plans.Add(plan);
        }

        return plans;
    }

    /// <summary>The accounts reduced to the readings taken on <paramref name="plan"/> (null = no plan known); a reading
    /// without a plan belongs to the account's current plan. Accounts left without a reading drop out.</summary>
    public static List<ForecastAccount> ForPlan(IReadOnlyList<ForecastAccount> accounts, string? plan)
    {
        var result = new List<ForecastAccount>();
        foreach (var account in accounts)
        {
            var current = CurrentPlanOf(account);
            var points = account.Points.Where(point => (string.IsNullOrWhiteSpace(point.Plan) ? current : point.Plan) == plan).ToList();
            if (points.Count > 0)
                result.Add(new ForecastAccount(account.Key, plan, points));
        }

        return result;
    }

    /// <summary>The forecast of a provider. With <paramref name="plan"/> only the readings taken on that plan count
    /// (see <see cref="BuildForPlan"/>); without it all readings do.</summary>
    public static ProviderForecast? Build(
        string providerId, IReadOnlyList<ForecastAccount> accounts, IReadOnlyList<StatsRecord> records,
        Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone, string? plan = null) =>
        plan is null
            ? BuildCore(providerId, accounts, accounts, null, false, records, pricer, now, zone)
            : BuildForPlan(providerId, accounts, plan, records, pricer, now, zone);

    /// <summary>The forecast of one plan (null = the readings without a known plan) of a provider's accounts. The
    /// accounts' usage is pooled in the logs, so the hourly intervals are built over ALL accounts and only the hours
    /// in which every account was on <paramref name="plan"/> count: another account's or an earlier plan's tokens
    /// never enter this plan's capacity or API value.</summary>
    public static ProviderForecast? BuildForPlan(
        string providerId, IReadOnlyList<ForecastAccount> accounts, string? plan, IReadOnlyList<StatsRecord> records,
        Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone) =>
        BuildCore(providerId, accounts, ForPlan(accounts, plan), plan, true, records, pricer, now, zone);

    private static ProviderForecast? BuildCore(
        string providerId, IReadOnlyList<ForecastAccount> all, IReadOnlyList<ForecastAccount> accounts, string? plan, bool forPlan,
        IReadOnlyList<StatsRecord> records, Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone)
    {
        var weekly = BuildWindow(WindowKind.Weekly, all, accounts, forPlan, plan, records, pricer, now, zone);
        var fiveHour = BuildWindow(WindowKind.FiveHour, all, accounts, forPlan, plan, records, pricer, now, zone);
        if (weekly is null && fiveHour is null)
            return null;

        var plans = accounts.Select(account => account.Plan).Where(plan => !string.IsNullOrWhiteSpace(plan))
            .Select(plan => plan!).Distinct().ToList();
        var prices = accounts.Select(account => PlanPriceList.Find(providerId, account.Plan)).ToList();
        var known = accounts.Count > 0 && prices.All(price => price is not null);
        double? planUsd = known && prices.All(price => !price!.IsEuro) ? prices.Sum(price => price!.Net) : null;
        double? monthlyApi = weekly?.ApiUsdPerWindow * DaysPerMonth / 7 * accounts.Count;
        var lastReading = accounts.SelectMany(account => account.Points).Select(point => (DateTimeOffset?)point.Timestamp).Max();
        return new ProviderForecast(providerId, fiveHour, weekly, plans, accounts.Count, monthlyApi, planUsd,
            known ? prices.Select(price => price!).ToList() : null, plan ?? (plans.Count == 1 ? plans[0] : null), true, lastReading);
    }

    /// <summary>The window of <paramref name="kind"/>. <paramref name="accounts"/> are the accounts (readings) of the forecast;
    /// for a plan forecast the intervals are pooled over <paramref name="all"/> accounts, so hours another account or
    /// plan used are not counted for this plan.</summary>
    private static ForecastWindow? BuildWindow(
        WindowKind kind, IReadOnlyList<ForecastAccount> all, IReadOnlyList<ForecastAccount> accounts, bool forPlan, string? plan,
        IReadOnlyList<StatsRecord> records, Func<IReadOnlyList<StatsRecord>, CostSummary>? pricer, DateTimeOffset now, TimeZoneInfo zone)
    {
        var lists = accounts.Select(account => PointsOf(account.Points, kind)).Where(points => points.Count > 0).ToList();
        if (lists.Count == 0)
            return null;

        IReadOnlyList<IReadOnlyList<HistoryPoint>> pooled = lists;
        IReadOnlyList<Func<DateTime, string?>>? planAt = null;
        if (forPlan)
        {
            var pool = all.Select(account => (Account: account, Points: PointsOf(account.Points, kind))).Where(entry => entry.Points.Count > 0).ToList();
            pooled = pool.Select(entry => (IReadOnlyList<HistoryPoint>)entry.Points).ToList();
            planAt = pool.Select(entry => PlanAtOf(entry.Account, entry.Points, zone)).ToList();
        }

        var intervals = StatsTokensPerPercent.BuildPooledIntervals(records, pooled, zone, pricer, planAt, plan);
        var result = StatsTokensPerPercent.Estimate(intervals);
        var average = AveragePeriod(lists, kind, records, now.AddDays(-StatsTokensPerPercent.LookbackDays), now, zone, pooled, planAt, plan);
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

    /// <summary>What <see cref="PlanAtOf"/> answers for an hour in which the plan changed: equal to no plan.</summary>
    private const string MixedPlan = "\u0001mixed";

    /// <summary>The plan an account was on in a whole clock hour of <paramref name="zone"/>: the plan of its newest reading
    /// at the hour's start (a reading without one counts for the account's current plan), or <see cref="MixedPlan"/>
    /// when the reading at the hour's end names another.</summary>
    private static Func<DateTime, string?> PlanAtOf(ForecastAccount account, IReadOnlyList<HistoryPoint> points, TimeZoneInfo zone)
    {
        var current = CurrentPlanOf(account);
        var sorted = points.OrderBy(point => point.Timestamp).ToList();

        string? At(DateTime wall)
        {
            var moment = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            int low = 0, high = sorted.Count - 1, found = 0;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (sorted[middle].Timestamp <= moment)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return string.IsNullOrWhiteSpace(sorted[found].Plan) ? current : sorted[found].Plan;
        }

        return hour =>
        {
            var start = At(hour);
            return start == At(hour.AddHours(1)) ? start : MixedPlan;
        };
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
        DateTimeOffset lookbackStart, DateTimeOffset now, TimeZoneInfo zone,
        IReadOnlyList<IReadOnlyList<HistoryPoint>>? pooledPoints = null, IReadOnlyList<Func<DateTime, string?>>? planAt = null, string? plan = null)
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
        var covered = StatsTokensPerPercent.BuildPooledIntervals(inRanges, pooledPoints ?? accountsPoints, zone, null, planAt, plan);
        var coveredPercent = covered.Sum(interval => interval.DeltaPercent);
        var coveredTokens = covered.Sum(interval => interval.TokensByModel.Values.Sum());
        if (!(coveredPercent > 0) || coveredTokens <= 0)
            return null;

        var tokens = coveredTokens / coveredPercent * peaks.Sum();
        return new ForecastAverage(peaks.Count, (long)(tokens / peaks.Count + 0.5), peaks.Average());
    }
}
