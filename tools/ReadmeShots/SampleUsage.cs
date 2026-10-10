using AiUsage.Stats;

namespace ReadmeShots;

/// <summary>Invented token usage for the statistics picture, shaped like a real developer's year:
/// Claude from about eleven months back, Codex joining later, quiet weekends with the odd burst,
/// holidays and sick days, uneven day sizes, a focus project per week, newer models replacing older
/// ones, and a busier last few weeks. Seeded, so every run draws the same picture.</summary>
internal static class SampleUsage
{
    private static readonly string[] Projects =
    [
        @"C:\Projects\checkout-service", @"C:\Projects\aurora-ui", @"C:\Projects\billing-api",
        @"C:\Projects\infra-terraform", @"C:\Projects\ml-pipeline", @"C:\Projects\landing-page",
    ];

    private static readonly double[] ProjectWeights = [0.30, 0.24, 0.18, 0.12, 0.10, 0.06];

    internal static IReadOnlyList<StatsRecord> Build(DateOnly today)
    {
        var random = new Random(20261008);
        var records = new List<StatsRecord>();
        var first = today.AddDays(-330);
        var codexStart = today.AddDays(-190);
        // A two week summer break about four months back.
        var summerBreakStart = today.AddDays(-125);
        var codexStreak = 0;
        var focusProject = 0;

        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var progress = (day.DayNumber - first.DayNumber) / 330.0;
            if (day.DayOfWeek == DayOfWeek.Monday || day == first)
                focusProject = PickWeighted(random, ProjectWeights);

            if (IsDayOff(day, today, summerBreakStart, random))
                continue;

            // Uneven day sizes (a log-normal spread) on a base that grows over the year and picks up
            // in the last six weeks, so the period comparison shows growth.
            var size = Math.Exp(0.6 * Gaussian(random));
            var dayTotal = 20_000_000 * (0.8 + 0.7 * progress) * size;
            dayTotal *= 1 + 0.35 * Math.Max(0, progress - 0.87) / 0.13;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend)
                dayTotal *= 0.15 + 0.5 * random.NextDouble();

            // Codex joins later and grows; now and then a run of Codex-heavy days, and some days
            // without Codex at all.
            var codexShare = 0.0;
            if (day >= codexStart)
            {
                var codexProgress = (day.DayNumber - codexStart.DayNumber) / 190.0;
                if (codexStreak == 0 && random.NextDouble() < 0.07)
                    codexStreak = random.Next(2, 6);
                if (codexStreak > 0)
                {
                    codexShare = 0.6 + 0.25 * random.NextDouble();
                    codexStreak--;
                }
                else if (random.NextDouble() > 0.3)
                {
                    codexShare = (0.1 + 0.3 * codexProgress) * (0.5 + random.NextDouble());
                }
            }

            var hours = DayHours(random, weekend);
            AddProviderDay(records, random, "claude", day, dayTotal * (1 - codexShare), progress, hours, focusProject);
            if (codexShare > 0)
                AddProviderDay(records, random, "codex", day, dayTotal * codexShare, progress, hours, focusProject);
        }
        AddGeminiDays(records, today);
        return records;
    }

    /// <summary>Gemini from the Antigravity CLI: a few working days a week over the last four months, on
    /// its own random stream so the other providers keep their picture.</summary>
    private static void AddGeminiDays(List<StatsRecord> records, DateOnly today)
    {
        var random = new Random(20261010);
        for (var day = today.AddDays(-120); day <= today; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || random.NextDouble() > 0.4)
                continue;

            var start = random.Next(9, 16);
            for (var hour = start; hour < start + random.Next(1, 4); hour++)
            {
                var total = 2_000_000 * (0.5 + random.NextDouble());
                var cacheRead = (long)(total * 0.9);
                var input = (long)(total * 0.03);
                var model = random.NextDouble() < 0.7 ? "gemini-3-pro" : "gemini-3-flash";
                records.Add(new StatsRecord(
                    "gemini", day, model, Projects[random.Next(Projects.Length)], input, (long)total - cacheRead - input, 0, cacheRead, hour, ""));
            }
        }
    }

    /// <summary>Most weekends, the summer break, the days around new year and a few sick days carry
    /// no usage. The last three weeks stay workdays, so the day detail always has one to show.</summary>
    private static bool IsDayOff(DateOnly day, DateOnly today, DateOnly summerBreakStart, Random random)
    {
        var offRoll = random.NextDouble();
        if (day > today.AddDays(-21))
            return day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && offRoll < 0.6;
        if (day >= summerBreakStart && day < summerBreakStart.AddDays(14))
            return true;
        if ((day.Month == 12 && day.Day >= 24) || (day.Month == 1 && day.Day <= 2))
            return true;
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return offRoll < 0.65;
        return offRoll < 0.06;
    }

    /// <summary>A weight per hour: two to four working sessions of one to three hours between eight
    /// and seven, and on some days a late session in the evening.</summary>
    private static double[] DayHours(Random random, bool weekend)
    {
        var hours = new double[24];
        var sessions = weekend ? random.Next(1, 3) : random.Next(2, 5);
        for (var session = 0; session < sessions; session++)
        {
            var start = random.Next(weekend ? 10 : 8, 18);
            var length = random.Next(1, 4);
            for (var hour = start; hour < Math.Min(24, start + length); hour++)
                hours[hour] += 0.5 + random.NextDouble();
        }
        if (random.NextDouble() < 0.15)
            hours[random.Next(20, 24)] += 0.4 + 0.6 * random.NextDouble();
        return hours;
    }

    private static void AddProviderDay(
        List<StatsRecord> records, Random random, string provider, DateOnly day, double total, double progress,
        double[] hours, int focusProject)
    {
        var weightSum = hours.Sum();
        var cacheShare = 0.80 + 0.1 * random.NextDouble();
        for (var hour = 0; hour < hours.Length; hour++)
        {
            if (hours[hour] <= 0)
                continue;

            var sliceTotal = total * hours[hour] / weightSum;
            var project = Projects[random.NextDouble() < 0.6 ? focusProject : PickWeighted(random, ProjectWeights)];
            var (model, effort) = PickModel(random, provider, progress);

            var cacheRead = (long)(sliceTotal * cacheShare);
            var cacheWrite = (long)(sliceTotal * (0.95 - cacheShare) * 0.7);
            var input = (long)(sliceTotal * 0.02);
            var output = (long)sliceTotal - cacheRead - cacheWrite - input;
            records.Add(new StatsRecord(provider, day, model, project, input, output, cacheWrite, cacheRead, hour, effort));
        }
    }

    /// <summary>Newer models take over during the year: Opus 4.8 and Sonnet 5 replace the 4.6
    /// models, GPT-5.6 Sol replaces Codex's first model, with a little Haiku for quick jobs.</summary>
    private static (string Model, string Effort) PickModel(Random random, string provider, double progress)
    {
        var roll = random.NextDouble();
        if (provider == "codex")
        {
            var model = roll < Math.Clamp((progress - 0.55) * 2.5, 0, 0.9) ? "gpt-5.6-sol" : "gpt-5-codex";
            return (model, random.NextDouble() < 0.65 ? "medium" : "high");
        }

        var newer = progress > 0.5 && random.NextDouble() < Math.Clamp((progress - 0.5) * 3, 0, 0.95);
        var claudeModel = roll switch
        {
            < 0.06 => "claude-haiku-4-5",
            < 0.62 => newer ? "claude-opus-4-8" : "claude-opus-4-6",
            _ => newer ? "claude-sonnet-5" : "claude-sonnet-4-6",
        };
        return (claudeModel, random.NextDouble() < 0.55 ? "high" : "medium");
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
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

    /// <summary>The most varied weekday in the window a little over a week back (both agents, then
    /// the most models, projects and hours), so the day detail beneath the year grid has plenty to
    /// show.</summary>
    internal static DateOnly PickBusyWeekday(IReadOnlyList<StatsRecord> records, DateOnly today) =>
        records
            .Where(record => record.Day >= today.AddDays(-16) && record.Day <= today.AddDays(-8)
                && record.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .GroupBy(record => record.Day)
            .OrderByDescending(group => group.Select(record => record.Provider).Distinct().Count())
            .ThenByDescending(group => group.Select(record => record.Model).Distinct().Count()
                + group.Select(record => record.Project).Distinct().Count()
                + group.Select(record => record.Hour).Distinct().Count())
            .ThenByDescending(group => group.Sum(record => record.TotalTokens))
            .First().Key;
}
