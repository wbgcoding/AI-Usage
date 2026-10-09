using System.IO;
using AiUsage.Models;
using AiUsage.Storage;

/// <summary>Quota history for the statistics window's limits section: a few weekly and five-hour
/// periods per provider, some of them ending at the limit, written to a throw-away data folder.</summary>
internal static class SampleLimits
{
    private static readonly double[] ClaudeWeeklyPeaks = [100, 87, 64, 100];
    private static readonly double[] CodexWeeklyPeaks = [58, 71, 36, 49];
    private static readonly double[] FiveHourPeaks = [100, 72, 100, 55, 88, 100];

    private sealed record Reading(DateTimeOffset At, string Provider, WindowKind Window, double Percent, DateTimeOffset Reset);

    internal static void Seed(string dataFolder, DateTimeOffset now)
    {
        Directory.CreateDirectory(dataFolder);
        var readings = new List<Reading>();

        void Climb(string provider, WindowKind window, DateTimeOffset reset, TimeSpan length, double peak)
        {
            for (var step = 1; step <= 6; step++)
                readings.Add(new Reading(reset - length + length * step / 7, provider, window, Math.Round(peak * step / 6, 1), reset));
        }

        for (var week = 0; week < ClaudeWeeklyPeaks.Length; week++)
        {
            var reset = now.AddDays(1 - 7 * week);
            Climb("claude", WindowKind.Weekly, reset, TimeSpan.FromDays(7), ClaudeWeeklyPeaks[week]);
            Climb("codex", WindowKind.Weekly, reset.AddHours(5), TimeSpan.FromDays(7), CodexWeeklyPeaks[week]);
        }

        for (var block = 0; block < FiveHourPeaks.Length; block++)
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
