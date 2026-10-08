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
                Snapshot("claude", "Max 5x", SourceKind.LocalFile, TimeSpan.FromSeconds(12),
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, In(new TimeSpan(2, 14, 0)), 300, new TokenUsage(1_823_457)),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, In(new TimeSpan(3, 4, 0, 0)), 10080)),
                ClaudeHistory(now)),
            new("codex", "Codex",
                Snapshot("codex", "Pro", SourceKind.LocalFile, TimeSpan.FromSeconds(31),
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 18, In(new TimeSpan(3, 40, 0)), 300),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, 35, In(new TimeSpan(5, 2, 0, 0)), 10080)),
                null),
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

    /// <summary>Seven days at half hour steps: the five hour window swells and settles with the
    /// working day (ending exactly on the shown 42 percent), the weekly one climbs smoothly to 63.</summary>
    private static IReadOnlyList<HistoryPoint> ClaudeHistory(DateTimeOffset now)
    {
        var points = new List<HistoryPoint>();
        var start = now - TimeSpan.FromDays(7);
        const int steps = 7 * 48;
        var dayWeights = new[] { 0.85, 1.0, 0.7, 0.95, 0.6, 0.3, 0.75 };

        double FiveHour(DateTimeOffset at)
        {
            var hour = at.LocalDateTime.TimeOfDay.TotalHours;
            var activity = Math.Max(0, Math.Sin(Math.PI * (hour - 7.5) / 13));
            var dayIndex = (int)Math.Floor((at - start).TotalDays) % 7;
            return 78 * Math.Pow(activity, 1.4) * dayWeights[dayIndex];
        }

        var fiveHourAtNow = FiveHour(now);
        for (var step = 0; step <= steps; step++)
        {
            var at = start + TimeSpan.FromMinutes(30 * step);
            var blend = Math.Exp(-(now - at).TotalHours / 3);
            var fiveHour = Math.Clamp(FiveHour(at) + (42 - fiveHourAtNow) * blend, 0, 100);

            var progress = step / (double)steps;
            var weekly = 63 * Math.Pow(progress, 0.85) + 9 * (1 - progress) + 1.5 * Math.Sin(progress * 14);
            weekly = step == steps ? 63 : Math.Clamp(weekly, 0, 100);

            points.Add(new HistoryPoint(1, at, WindowKind.FiveHour, Math.Round(fiveHour, 1), null));
            points.Add(new HistoryPoint(1, at, WindowKind.Weekly, Math.Round(weekly, 1), null));
        }
        return points;
    }
}
