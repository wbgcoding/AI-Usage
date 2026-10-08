using AiUsage.Views.Controls;

namespace AiUsage.Services;

/// <summary>
/// Projects when a usage window will reach 100% at its current pace - a straight-line fit over its
/// own recent history. Every refusal rule is checked before any answer is built: too few points,
/// too short a real-time span, or a flat/falling trend all mean "no confident guess" rather than a
/// number nobody should trust.
/// </summary>
public static class UsageForecast
{
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(15);

    // A fall larger than rounding noise between two readings means the window reset.
    private const double ResetDropPercent = 5;

    /// <summary>
    /// <paramref name="lookback"/> is how far back <paramref name="points"/> is trimmed before
    /// fitting - the caller passes a window-appropriate value (2h for a five-hour window, 24h for a
    /// weekly one); null means "use everything handed in". <paramref name="resetsAt"/> caps the
    /// answer: a projection landing after the window resets is reported as null, since the window
    /// resets first - a reassuring answer, not a missing one.
    /// </summary>
    public static TimeSpan? TimeToFull(
        IReadOnlyList<HistoryChart.ChartPoint> points, DateTimeOffset now, DateTimeOffset? resetsAt,
        int minimumPoints = 6, TimeSpan? lookback = null)
    {
        var windowStart = lookback is { } span ? now - span : DateTimeOffset.MinValue;
        var recent = points.Where(p => p.At >= windowStart && p.At <= now).OrderBy(p => p.At).ToList();

        // Usage only falls when its window resets. Points from before the last such fall belong to
        // the window that is over and would pull the fitted slope down, hiding a fast new window.
        for (var i = recent.Count - 1; i > 0; i--)
        {
            if (recent[i].Percent < recent[i - 1].Percent - ResetDropPercent)
            {
                recent.RemoveRange(0, i);
                break;
            }
        }

        if (recent.Count < minimumPoints)
            return null;

        var first = recent[0].At;
        var last = recent[^1];
        if (last.At - first < MinimumSpan)
            return null;

        var meanX = recent.Average(p => (p.At - first).TotalSeconds);
        var meanY = recent.Average(p => p.Percent);

        double numerator = 0;
        double denominator = 0;
        foreach (var point in recent)
        {
            var x = (point.At - first).TotalSeconds - meanX;
            numerator += x * (point.Percent - meanY);
            denominator += x * x;
        }

        if (denominator == 0)
            return null; // every point at the same instant - nothing to fit a trend to

        var slopePercentPerSecond = numerator / denominator;
        // Below this the "trend" is floating-point noise on a flat series, and dividing by it would
        // overflow TimeSpan.
        if (slopePercentPerSecond <= 1e-9)
            return null; // flat or falling - nothing to warn about

        if (last.Percent >= 100)
            return TimeSpan.Zero;

        var seconds = (100 - last.Percent) / slopePercentPerSecond;
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
            return null;
        var projected = TimeSpan.FromSeconds(seconds);
        if (resetsAt is { } reset && projected > reset - now)
            return null; // the window resets before it would ever fill

        return projected;
    }
}
