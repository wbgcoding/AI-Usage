using AiUsage.Models;
using AiUsage.Services;

namespace ReadmeShots;

/// <summary>The invented numbers behind the widget pictures: one finished snapshot per provider, in
/// the shape the real providers hand over, and a smooth week of history for the Claude tile.</summary>
internal static class SampleProviders
{
    internal sealed record Sample(
        string Id, string DisplayName, ProviderSnapshot Snapshot, IReadOnlyList<HistoryPoint>? History);

    /// <summary>Stands in for a real provider so the widget's view model builds its tiles without
    /// reading anything from this machine. It is never asked to fetch.</summary>
    internal sealed class Provider(Sample sample) : IUsageProvider
    {
        public string Id => sample.Id;

        public string DisplayName => sample.DisplayName;

        public bool CanSkipWebSession => sample.Id is "claude" or "codex" or "gemini";

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(sample.Snapshot);
    }

    internal static IReadOnlyList<Sample> Build(DateTimeOffset now)
    {
        // Half a minute on every countdown so the shown minute does not flip while the picture renders.
        DateTimeOffset In(TimeSpan span) => now + span + TimeSpan.FromSeconds(30);

        ProviderSnapshot Snapshot(
            string id, string plan, SourceKind source, TimeSpan age, params UsageWindow[] windows) =>
            new(id, windows, plan, source, now - age, now - age, ProviderStatus.Ok, null);

        return
        [
            new("claude", "Claude",
                Snapshot("claude", "Max", SourceKind.LocalFile, TimeSpan.FromSeconds(12),
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, In(new TimeSpan(2, 14, 0)), 300, new TokenUsage(1_823_457)),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, In(new TimeSpan(3, 4, 0, 0)), 10080))
                    with { ApiCredit = new ClaudeApiCredit(18.40m, "USD", new ClaudeCreditGrant(100m, 81.50m, now + TimeSpan.FromDays(12))) },
                History(now, fiveHourNow: 42, weeklyNow: 63, peak: 81, seed: 11)),
            new("codex", "Codex",
                Snapshot("codex", "Pro", SourceKind.LocalFile, TimeSpan.FromSeconds(31),
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 18, In(new TimeSpan(3, 40, 0)), 300),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, 35, In(new TimeSpan(5, 2, 0, 0)), 10080)),
                History(now, fiveHourNow: 18, weeklyNow: 35, peak: 52, seed: 23)),
            new("cursor", "Cursor",
                Snapshot("cursor", "Pro", SourceKind.WebSession, TimeSpan.FromSeconds(48),
                    new UsageWindow("Window_CursorModels", WindowKind.Other, 54, In(TimeSpan.FromDays(19)), null),
                    new UsageWindow("Window_OtherModels", WindowKind.Other, 12, In(TimeSpan.FromDays(19)), null)),
                null),
            new("gemini", "Gemini",
                Snapshot("gemini", "Pro", SourceKind.WebSession, TimeSpan.FromMinutes(2),
                    new UsageWindow("Gemini Pro", WindowKind.Other, 27, In(TimeSpan.FromHours(9)), null),
                    new UsageWindow("Gemini Flash", WindowKind.Other, 8, In(TimeSpan.FromHours(9)), null)),
                null),
            new("copilot", "Copilot",
                Snapshot("copilot", "Pro", SourceKind.LocalLogin, TimeSpan.FromSeconds(20),
                    new UsageWindow("Premium", WindowKind.Other, 37, In(TimeSpan.FromDays(19)), null)),
                null),
        ];
    }

    /// <summary>The review variant: the same five providers, but with a warn level window (72 percent),
    /// a critical one (93 percent), a weekly window 19 days out, one resetting in exactly 9 h 0 m and a
    /// provider that is not signed in, so a visual review sees every level.</summary>
    internal static IReadOnlyList<Sample> BuildReview(DateTimeOffset now)
    {
        DateTimeOffset In(TimeSpan span) => now + span + TimeSpan.FromSeconds(30);

        ProviderSnapshot Snapshot(
            string id, string plan, SourceKind source, TimeSpan age, params UsageWindow[] windows) =>
            new(id, windows, plan, source, now - age, now - age, ProviderStatus.Ok, null);

        var claude = Build(now).Single(sample => sample.Id == "claude");
        claude = claude with
        {
            Snapshot = claude.Snapshot with
            {
                Windows = [.. claude.Snapshot.Windows, new UsageWindow("Fable", WindowKind.Other, 21, In(TimeSpan.FromDays(3)), null)],
            },
        };
        return
        [
            claude,
            new("codex", "Codex",
                Snapshot("codex", "Pro", SourceKind.LocalFile, TimeSpan.FromSeconds(31),
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 18, In(new TimeSpan(3, 40, 0)), 300),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, 72, In(TimeSpan.FromDays(19)), 10080)),
                null),
            new("cursor", "Cursor",
                Snapshot("cursor", "Pro", SourceKind.WebSession, TimeSpan.FromSeconds(48),
                    new UsageWindow("Window_CursorModels", WindowKind.Other, 93, In(TimeSpan.FromDays(19)), null),
                    new UsageWindow("Window_OtherModels", WindowKind.Other, 12, In(TimeSpan.FromDays(19)), null)),
                null),
            new("gemini", "Gemini",
                Snapshot("gemini", "Pro", SourceKind.WebSession, TimeSpan.FromMinutes(2),
                    new UsageWindow("Gemini Pro", WindowKind.Other, 27, In(TimeSpan.FromHours(9)), null),
                    new UsageWindow("Gemini Flash", WindowKind.Other, 8, In(TimeSpan.FromHours(9)), null)),
                null),
            new("copilot", "Copilot",
                new ProviderSnapshot("copilot", [], null, SourceKind.LocalLogin, now, null, ProviderStatus.NotSignedIn, null),
                null),
        ];
    }

    /// <summary>Seven days at half hour steps from a seeded run of invented work sessions: each
    /// session starts at an uneven time, lasts an uneven while and burns tokens at an uneven pace. The
    /// five hour window opens with the first use and drops to zero five hours later; the weekly one
    /// climbs with every session and stays flat in between. Both end exactly on the shown percent.</summary>
    private static IReadOnlyList<HistoryPoint> History(DateTimeOffset now, double fiveHourNow, double weeklyNow, double peak, int seed)
    {
        const int stepsPerHour = 2;
        const int steps = 7 * 24 * stepsPerHour;
        var start = now - TimeSpan.FromDays(7);
        var random = new Random(seed);

        // Use per half hour step: one to three sessions a day, longer and busier on some days.
        var use = new double[steps + 1];
        for (var day = 0; day < 7; day++)
        {
            var dayLoad = 0.5 + random.NextDouble() * 0.7;
            var sessions = random.Next(1, 4);
            for (var session = 0; session < sessions; session++)
            {
                var from = day * 24 * stepsPerHour + (int)((8 + random.NextDouble() * 13) * stepsPerHour);
                var length = random.Next(4, 13);
                var pace = dayLoad * (0.6 + 0.6 * random.NextDouble());
                for (var step = from; step < Math.Min(from + length, steps + 1); step++)
                    use[step] += pace * (0.5 + random.NextDouble());
            }
        }

        // The five hour window: opened by the first use, cleared five hours later.
        var fiveHour = new double[steps + 1];
        var windowEnd = -1;
        double inWindow = 0;
        for (var step = 0; step <= steps; step++)
        {
            if (step >= windowEnd)
            {
                inWindow = 0;
                windowEnd = use[step] > 0 ? step + 5 * stepsPerHour : -1;
            }
            inWindow += use[step];
            fiveHour[step] = inWindow;
        }
        // Scaled on a typical busy window rather than the busiest, so most days rise visibly; the
        // few busier ones are capped at the limit like a real window.
        var windowPeaks = fiveHour.Where(value => value > 0).OrderBy(value => value).ToList();
        var typical = windowPeaks.Count > 0 ? windowPeaks[(int)(windowPeaks.Count * 0.95)] : 1;
        var fiveHourScale = peak / Math.Max(1e-9, typical);

        var weekly = new double[steps + 1];
        double total = 0;
        for (var step = 0; step <= steps; step++)
        {
            total += use[step];
            weekly[step] = total;
        }

        var points = new List<HistoryPoint>();
        var fiveHourEnd = fiveHour[steps] * fiveHourScale;
        for (var step = 0; step <= steps; step++)
        {
            var at = start + TimeSpan.FromMinutes(30 * step);
            // The last hours lean toward the shown percent, so the line meets the bar.
            var blend = Math.Exp(-(now - at).TotalHours / 2);
            var five = Math.Clamp(Math.Min(100, fiveHour[step] * fiveHourScale) + (fiveHourNow - Math.Min(100, fiveHourEnd)) * blend, 0, 100);
            var week = 6 + (weeklyNow - 6) * weekly[step] / Math.Max(1e-9, total);
            if (step == steps)
            {
                five = fiveHourNow;
                week = weeklyNow;
            }
            points.Add(new HistoryPoint(1, at, WindowKind.FiveHour, Math.Round(five, 1), null));
            points.Add(new HistoryPoint(1, at, WindowKind.Weekly, Math.Round(week, 1), null));
        }
        return points;
    }
}
