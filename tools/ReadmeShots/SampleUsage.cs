using AiUsage.Stats;

namespace ReadmeShots;

/// <summary>Twelve months of invented token usage for the statistics picture: a Claude and a Codex
/// session on most weekdays, lighter and rarer on weekends, growing over the year, mostly cache reads.</summary>
internal static class SampleUsage
{
    private static readonly string[] Projects =
    [
        @"C:\Projects\web-shop", @"C:\Projects\api-server", @"C:\Projects\mobile-app", @"C:\Projects\docs-site",
    ];

    private static readonly double[] ProjectWeights = [0.38, 0.30, 0.20, 0.12];

    // Working hours carry most of the day, with a quiet start and a lunch dip, so the hour chart
    // reads as a calm curve instead of a few spikes.
    private static readonly double[] HourWeights =
    [
        0, 0, 0, 0, 0, 0, 0, 0.2, 0.6, 1.0, 1.2, 1.1, 0.6, 0.9, 1.2, 1.3, 1.1, 0.9, 0.6, 0.4, 0.3, 0.2, 0.1, 0,
    ];

    internal static IReadOnlyList<StatsRecord> Build(DateOnly today)
    {
        var random = new Random(20260702);
        var records = new List<StatsRecord>();
        var first = today.AddDays(-364);

        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var progress = (day.DayNumber - first.DayNumber) / 364.0;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            // A steady rise over the year, a slow few-week swell on top and modest day-to-day noise:
            // every day has some usage, weekends a lighter share of it.
            var swell = 1 + 0.06 * Math.Sin(day.DayNumber / 9.0);
            var noise = 0.8 + 0.4 * random.NextDouble();
            var dayTotal = 30_000_000 * (0.6 + 0.6 * progress) * swell * noise;
            // The last six weeks pick up pace, so the period comparison shows growth.
            dayTotal *= 1 + 0.3 * Math.Max(0, progress - 0.88) / 0.12;
            if (weekend)
                dayTotal *= 0.3 + 0.15 * random.NextDouble();

            // Claude leads most days; every sixth week is a Codex-heavy stretch.
            var codexWeek = day.DayNumber / 7 % 6 == 3;
            var claudeShare = (codexWeek ? 0.36 : 0.66) + 0.08 * (random.NextDouble() - 0.5);
            AddProviderDay(records, random, "claude", day, dayTotal * claudeShare);
            AddProviderDay(records, random, "codex", day, dayTotal * (1 - claudeShare));
        }
        return records;
    }

    private static void AddProviderDay(List<StatsRecord> records, Random random, string provider, DateOnly day, double total)
    {
        var weightSum = HourWeights.Sum();
        for (var hour = 0; hour < HourWeights.Length; hour++)
        {
            if (HourWeights[hour] <= 0)
                continue;

            var sliceTotal = total * HourWeights[hour] / weightSum * (0.85 + 0.3 * random.NextDouble());
            var project = Projects[PickWeighted(random, ProjectWeights)];
            var (model, effort) = provider == "claude"
                ? (random.NextDouble() < 0.62 ? "claude-opus-4-6" : "claude-sonnet-4-6", random.NextDouble() < 0.6 ? "high" : "medium")
                : ("gpt-5-codex", random.NextDouble() < 0.7 ? "medium" : "high");

            var cacheRead = (long)(sliceTotal * 0.86);
            var cacheWrite = (long)(sliceTotal * 0.08);
            var input = (long)(sliceTotal * 0.02);
            var output = (long)sliceTotal - cacheRead - cacheWrite - input;
            records.Add(new StatsRecord(provider, day, model, project, input, output, cacheWrite, cacheRead, hour, effort));
        }
    }

    private static int PickWeighted(Random random, double[] weights)
    {
        var roll = random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
                return i;
        }
        return weights.Length - 1;
    }

    /// <summary>The busiest weekday in the window a little over a week back, so the day detail
    /// beneath the year grid has plenty to show.</summary>
    internal static DateOnly PickBusyWeekday(IReadOnlyList<StatsRecord> records, DateOnly today) =>
        records
            .Where(record => record.Day >= today.AddDays(-13) && record.Day <= today.AddDays(-8)
                && record.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .GroupBy(record => record.Day)
            .OrderByDescending(group => group.Sum(record => record.TotalTokens))
            .First().Key;
}
