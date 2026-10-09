using AiUsage.Models;

namespace AiUsage.Stats;

/// <summary>One whole hour inside a weekly quota period: how far the quota moved during it and which
/// tokens were used in it. Cache reads are kept apart because they weigh far less than other tokens.</summary>
public sealed record PerPercentInterval(double DeltaPercent, IReadOnlyDictionary<string, long> TokensByModel, long CacheReadTokens);

/// <summary>One model's share of the estimate: how many of its tokens fill one percent of the week.</summary>
public sealed record PerPercentItem(string Model, bool IsOther, long TokensPerPercent);

/// <summary>How far the estimate got: too little data, a per-model fit that holds up, or only a
/// single figure for all models together.</summary>
public enum PerPercentStatus
{
    TooFew,
    PerModel,
    Total,
}

/// <summary>The outcome of <see cref="StatsTokensPerPercent.Estimate"/>. <see cref="CacheReadPerPercent"/>
/// and <see cref="TotalPerPercent"/> are 0 when there is no figure to give.</summary>
public sealed record PerPercentResult(
    PerPercentStatus Status, int IntervalCount, IReadOnlyList<PerPercentItem> Items, long CacheReadPerPercent, long TotalPerPercent)
{
    public static PerPercentResult TooFew(int intervalCount) => new(PerPercentStatus.TooFew, intervalCount, [], 0, 0);
}

/// <summary>
/// Estimates how many tokens one percent of a provider's weekly quota costs. Every whole hour inside
/// one weekly period with a quota reading at both ends gives one equation: the quota's rise in that
/// hour equals the tokens of each model times that model's cost per token, plus the cache reads times
/// their own cost. Solving all hours at once (non-negative least squares) gives the costs; the inverse
/// is the tokens per percent. The fit is only trusted when it explains most of the variation.
/// </summary>
public static class StatsTokensPerPercent
{
    /// <summary>How far back the history and the token index are read for the estimate.</summary>
    public const int LookbackDays = 84;

    internal const int MinIntervals = 20;
    internal const double MinRSquared = 0.6;
    internal const int MaxModels = 6;
    internal const double MinShare = 0.05;

    /// <summary>A quota reading further than this before a whole hour no longer stands for that hour.</summary>
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(15);

    /// <summary>Builds the hourly intervals of <paramref name="weeklyPoints"/> (one provider's weekly
    /// quota history) against that provider's token <paramref name="records"/>. Hours without tokens, or
    /// without a reading at both ends, are left out.</summary>
    public static IReadOnlyList<PerPercentInterval> BuildIntervals(IReadOnlyList<StatsRecord> records, IReadOnlyList<HistoryPoint> weeklyPoints)
    {
        var models = new Dictionary<DateTime, Dictionary<string, long>>();
        var cacheReads = new Dictionary<DateTime, long>();
        foreach (var record in records)
        {
            var hour = record.Day.ToDateTime(new TimeOnly(record.Hour, 0));
            if (!models.TryGetValue(hour, out var perModel))
            {
                perModel = [];
                models[hour] = perModel;
            }

            var model = ModelDisplayNames.Resolve(record.Model);
            perModel[model] = perModel.GetValueOrDefault(model) + record.InputTokens + record.OutputTokens + record.CacheCreationTokens;
            cacheReads[hour] = cacheReads.GetValueOrDefault(hour) + record.CacheReadTokens;
        }

        var intervals = new List<PerPercentInterval>();
        foreach (var period in StatsLimitsAggregator.SplitIntoPeriods(weeklyPoints))
        {
            if (period[0].ResetsAt is null)
                continue;

            var readings = period.OrderBy(point => point.Timestamp).ToList();
            var first = readings[0].Timestamp.LocalDateTime;
            var last = readings[^1].Timestamp.LocalDateTime;
            var hour = new DateTime(first.Year, first.Month, first.Day, first.Hour, 0, 0, DateTimeKind.Unspecified);
            if (hour < first)
                hour = hour.AddHours(1);
            for (; hour.AddHours(1) <= last; hour = hour.AddHours(1))
            {
                if (!models.TryGetValue(hour, out var perModel))
                    continue;
                var cacheRead = cacheReads.GetValueOrDefault(hour);
                if (perModel.Values.Sum() + cacheRead == 0)
                    continue;
                if (ReadingAt(readings, new DateTimeOffset(hour)) is not { } before
                    || ReadingAt(readings, new DateTimeOffset(hour.AddHours(1))) is not { } after
                    || after < before)
                {
                    continue;
                }

                intervals.Add(new PerPercentInterval(after - before, perModel, cacheRead));
            }
        }

        return intervals;
    }

    /// <summary>The quota at <paramref name="moment"/>: the newest reading at or before it, if it is recent enough.</summary>
    private static double? ReadingAt(List<HistoryPoint> readings, DateTimeOffset moment)
    {
        var low = 0;
        var high = readings.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (readings[middle].Timestamp <= moment)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && moment - readings[found].Timestamp <= MaxReadingAge ? readings[found].Percent : null;
    }

    /// <summary>Fits the intervals and decides how much of the result to trust.</summary>
    public static PerPercentResult Estimate(IReadOnlyList<PerPercentInterval> intervals)
    {
        var count = intervals.Count;
        var totalPercent = intervals.Sum(interval => interval.DeltaPercent);
        if (count < MinIntervals || totalPercent <= 0)
            return PerPercentResult.TooFew(count);

        var modelTotals = new Dictionary<string, long>();
        foreach (var interval in intervals)
        {
            foreach (var (model, tokens) in interval.TokensByModel)
                modelTotals[model] = modelTotals.GetValueOrDefault(model) + tokens;
        }

        var allTokens = modelTotals.Values.Sum();
        if (allTokens == 0)
            return PerPercentResult.TooFew(count);

        var totalPerPercent = (long)(allTokens / totalPercent + 0.5);
        var total = new PerPercentResult(PerPercentStatus.Total, count, [], 0, totalPerPercent);

        // The largest models get a column each; the rest share one.
        var named = modelTotals.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxModels).Select(entry => entry.Key).ToList();
        var hasOther = modelTotals.Count > named.Count;
        var columnCount = named.Count + (hasOther ? 1 : 0) + 1;
        var cacheColumn = columnCount - 1;
        var otherColumn = hasOther ? named.Count : -1;

        var rows = new List<double[]>(count);
        foreach (var interval in intervals)
        {
            var row = new double[columnCount];
            foreach (var (model, tokens) in interval.TokensByModel)
            {
                var column = named.IndexOf(model);
                row[column >= 0 ? column : otherColumn] += tokens;
            }

            row[cacheColumn] = interval.CacheReadTokens;
            rows.Add(row);
        }

        var target = intervals.Select(interval => interval.DeltaPercent).ToList();
        var weights = Nnls.Solve(rows, target);
        if (RSquared(rows, target, weights) < MinRSquared)
            return total;

        var items = new List<PerPercentItem>();
        for (var column = 0; column < cacheColumn; column++)
        {
            var isOther = column == otherColumn;
            var columnTokens = isOther
                ? modelTotals.Where(entry => !named.Contains(entry.Key)).Sum(entry => entry.Value)
                : modelTotals[named[column]];
            if ((double)columnTokens / allTokens < MinShare || weights[column] <= 0)
                continue;

            items.Add(new PerPercentItem(isOther ? "" : named[column], isOther, (long)(1 / weights[column] + 0.5)));
        }

        if (items.Count == 0)
            return total;

        var cachePerPercent = weights[cacheColumn] > 0 ? (long)(1 / weights[cacheColumn] + 0.5) : 0;
        return new PerPercentResult(PerPercentStatus.PerModel, count, items, cachePerPercent, totalPerPercent);
    }

    /// <summary>The share of the variation in <paramref name="target"/> the fit explains.</summary>
    internal static double RSquared(IReadOnlyList<double[]> rows, IReadOnlyList<double> target, double[] weights)
    {
        var mean = target.Average();
        double residual = 0;
        double spread = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var predicted = 0.0;
            for (var j = 0; j < weights.Length; j++)
                predicted += rows[i][j] * weights[j];
            residual += (target[i] - predicted) * (target[i] - predicted);
            spread += (target[i] - mean) * (target[i] - mean);
        }

        return spread <= 0 ? 0 : 1 - residual / spread;
    }
}
