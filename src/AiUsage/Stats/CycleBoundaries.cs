using AiUsage.Models;

namespace AiUsage.Stats;

/// <summary>How a quota reading relates to the one before it in the same series.</summary>
public enum CycleBoundary
{
    /// <summary>Same cycle: the window keeps counting up.</summary>
    None,

    /// <summary>The window's regular reset: its reset time moved on.</summary>
    Regular,

    /// <summary>The window was cleared before its time (a bought or used limit reset): the reset time stayed,
    /// the value fell far.</summary>
    EarlyReset,

    /// <summary>The account changed plan between the two readings; the old cycle ends there.</summary>
    PlanChange,
}

/// <summary>
/// The one place that decides where a quota cycle ends and the next begins, from the stored history
/// alone (no provider reports an early reset explicitly). A series is one account's readings of one
/// window kind and label, in time order. Pure: no clock, no file access.
/// </summary>
public static class CycleBoundaries
{
    /// <summary>A reset time that moved by more than this is a regular reset; less is jitter (a provider
    /// derives the time from "now plus seconds left", so it wobbles between reads).</summary>
    internal static readonly TimeSpan RegularMove = TimeSpan.FromHours(1);

    /// <summary>The value before an early reset must have been at least this high.</summary>
    internal const double MinPercentBefore = 20;

    /// <summary>An early reset leaves at most this share of the value before it.</summary>
    internal const double MaxShareAfter = 0.5;

    /// <summary>An early reset lowers the value by at least this many points.</summary>
    internal const double MinDrop = 15;

    /// <summary>How <paramref name="next"/> relates to <paramref name="previous"/>, the reading before it in the same
    /// series. A stored early-reset flag on <paramref name="next"/> is honoured; a reading without it is judged
    /// from the two values. An unknown plan (null) never counts as a change, and a reading without a reset time
    /// can only show a plan change.</summary>
    public static CycleBoundary Classify(HistoryPoint previous, HistoryPoint next)
    {
        if (previous.Plan is { Length: > 0 } before && next.Plan is { Length: > 0 } after
            && !string.Equals(before, after, StringComparison.Ordinal))
        {
            return CycleBoundary.PlanChange;
        }

        if (next.EarlyReset == true)
            return CycleBoundary.EarlyReset;

        if (previous.ResetsAt is not { } was || next.ResetsAt is not { } now)
            return CycleBoundary.None;

        if ((now - was).Duration() > RegularMove)
            return CycleBoundary.Regular;

        return previous.Percent >= MinPercentBefore
            && next.Percent <= previous.Percent * MaxShareAfter
            && previous.Percent - next.Percent >= MinDrop
            ? CycleBoundary.EarlyReset
            : CycleBoundary.None;
    }

    /// <summary>The boundary before each reading of <paramref name="series"/> (time order); the first reading has
    /// none.</summary>
    public static IReadOnlyList<CycleBoundary> Kinds(IReadOnlyList<HistoryPoint> series)
    {
        var kinds = new CycleBoundary[series.Count];
        for (var i = 1; i < series.Count; i++)
            kinds[i] = Classify(series[i - 1], series[i]);
        return kinds;
    }
}
