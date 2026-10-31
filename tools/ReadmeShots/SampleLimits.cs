using System.IO;
using AiUsage.Models;
using AiUsage.Storage;

/// <summary>Quota history for the statistics window's limits section: a few weekly and five-hour
/// periods per provider, some of them ending at the limit, written to a throw-away data folder. With
/// the usage records, Claude's and Codex's quotas get an hourly reading that follows those tokens instead, so the
/// plan forecast has enough hours to work from.</summary>
internal static class SampleLimits
{
    private static readonly double[] ClaudeWeeklyPeaks = [100, 87, 64, 100];
    private static readonly double[] CodexWeeklyPeaks = [58, 71, 36, 49];
    private static readonly double[] FiveHourPeaks = [100, 72, 100, 55, 88, 100];

    private sealed record Reading(DateTimeOffset At, string Provider, WindowKind Window, double Percent, DateTimeOffset Reset);

    /// <summary>One reading on every full hour of the last six weeks for each provider's weekly and five-hour
    /// quotas. A reading is the share of a fixed allowance that the provider's tokens since the period began
    /// have used, so each hour's movement follows that hour's tokens; it restarts at every reset.</summary>
    private static void AddHourly(List<Reading> readings, DateTimeOffset now, IReadOnlyList<AiUsage.Stats.StatsRecord> records, string provider)
    {
        var perHour = new Dictionary<DateTime, long>();
        foreach (var record in records.Where(record => record.Provider == provider))
        {
            var hour = record.Day.ToDateTime(new TimeOnly(record.Hour, 0));
            perHour[hour] = perHour.GetValueOrDefault(hour) + record.TotalTokens;
        }

        var local = now.LocalDateTime;
        var thisHour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
        AddHourly(readings, provider, perHour, WindowKind.Weekly, TimeSpan.FromDays(7), local.AddDays(1), thisHour.AddDays(-41), local);
        AddHourly(readings, provider, perHour, WindowKind.FiveHour, TimeSpan.FromHours(5), thisHour, thisHour.AddDays(-41), local);
    }

    private static void AddHourly(
        List<Reading> readings, string provider, Dictionary<DateTime, long> perHour, WindowKind window, TimeSpan length,
        DateTime anchor, DateTime first, DateTime last)
    {
        long BlockOf(DateTime hour) => (long)Math.Floor((hour - anchor) / length);
        var totals = new Dictionary<long, long>();
        for (var hour = first; hour <= last; hour = hour.AddHours(1))
            totals[BlockOf(hour)] = totals.GetValueOrDefault(BlockOf(hour)) + perHour.GetValueOrDefault(hour);

        // One allowance for every period, so the busiest one ends near 97 percent.
        var allowance = Math.Max(1, totals.Values.Max() / 0.97);
        var used = new Dictionary<long, long>();
        for (var hour = first; hour <= last; hour = hour.AddHours(1))
        {
            var block = BlockOf(hour);
            var soFar = used.GetValueOrDefault(block);
            var reset = new DateTimeOffset(anchor + length * (block + 1));
            readings.Add(new Reading(new DateTimeOffset(hour), provider, window, (long)(soFar / allowance * 1000 + 0.5) / 10.0, reset));
            used[block] = soFar + perHour.GetValueOrDefault(hour);
        }
    }

    internal static void Seed(string dataFolder, DateTimeOffset now, IReadOnlyList<AiUsage.Stats.StatsRecord>? records = null)
    {
        Directory.CreateDirectory(dataFolder);
        var readings = new List<Reading>();

        void Climb(string provider, WindowKind window, DateTimeOffset reset, TimeSpan length, double peak)
        {
            for (var step = 1; step <= 6; step++)
                readings.Add(new Reading(reset - length + length * step / 7, provider, window, Math.Round(peak * step / 6, 1), reset));
        }

        if (records is not null)
        {
            AddHourly(readings, now, records, "claude");
            AddHourly(readings, now, records, "codex");
        }

        for (var week = 0; week < ClaudeWeeklyPeaks.Length; week++)
        {
            var reset = now.AddDays(1 - 7 * week);
            if (records is null)
                Climb("claude", WindowKind.Weekly, reset, TimeSpan.FromDays(7), ClaudeWeeklyPeaks[week]);
            if (records is null)
                Climb("codex", WindowKind.Weekly, reset.AddHours(5), TimeSpan.FromDays(7), CodexWeeklyPeaks[week]);
        }

        for (var block = 0; block < FiveHourPeaks.Length && records is null; block++)
            Climb("claude", WindowKind.FiveHour, now.AddHours(-30 * block), TimeSpan.FromHours(5), FiveHourPeaks[block]);

        // The history file only ever grows at its end, so the readings go in oldest first.
        var clock = now;
        var store = new HistoryStore(dataFolder, () => clock);
        foreach (var reading in readings.Where(reading => reading.At <= now).OrderBy(reading => reading.At))
        {
            clock = reading.At;
            store.Append(reading.Provider, reading.Window, reading.Percent, reading.Reset);
        }
    }
}
