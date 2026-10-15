using AiUsage.Models;
using System.Windows.Media;

namespace AiUsage.Stats;

/// <summary>The tokens one model used as main agent or as subagent within a window's slice of the
/// index (see <see cref="StatsStore.SumTokensByModelSince"/>).</summary>
public readonly record struct WindowShareRow(string Model, bool Subagent, long Tokens);

/// <summary>One colored part of a bar's filled section: its share of the window's tokens in percent
/// (0-100, all parts of a bar add up to 100) and the color the token window gives that model or
/// agent.</summary>
public readonly record struct BarSegment(string Label, double SharePercent, Color Color);

/// <summary>Turns a window's token rows into the colored parts of a bar, with the very colors the
/// statistics window uses: a model gets <see cref="ChartPalette.ForModel"/> by its rank among the
/// models of that window (largest first, like the model ring), main agent and subagent the first two
/// <see cref="ChartPalette.Categorical"/> colors (like the agent stack).</summary>
public static class WindowShares
{
    /// <summary>The parts for <paramref name="mode"/> (Agent or Model), largest share first; empty for
    /// the level mode, a mode the provider does not offer, or no tokens at all.</summary>
    public static IReadOnlyList<BarSegment> Build(
        string mode, string realProviderId, IReadOnlyList<WindowShareRow> rows, string mainLabel, string subagentLabel)
    {
        var total = rows.Sum(row => row.Tokens);
        if (total <= 0 || BarColorModes.Normalize(mode, realProviderId) != mode)
            return [];

        if (mode == BarColorModes.Model)
        {
            return rows
                .GroupBy(row => ModelDisplayNames.Resolve(row.Model))
                .Select(group => (Label: group.Key, Tokens: group.Sum(row => row.Tokens)))
                .OrderByDescending(entry => entry.Tokens)
                .ThenBy(entry => entry.Label, StringComparer.Ordinal)
                .Select((entry, rank) => new BarSegment(entry.Label, entry.Tokens * 100.0 / total, ChartPalette.ForModel(realProviderId, rank)))
                .ToList();
        }

        if (mode == BarColorModes.Agent)
        {
            var subagent = rows.Where(row => row.Subagent).Sum(row => row.Tokens);
            var parts = new List<BarSegment>();
            if (total - subagent > 0)
                parts.Add(new BarSegment(mainLabel, (total - subagent) * 100.0 / total, ChartPalette.Categorical(0)));
            if (subagent > 0)
                parts.Add(new BarSegment(subagentLabel, subagent * 100.0 / total, ChartPalette.Categorical(1)));
            return [.. parts.OrderByDescending(part => part.SharePercent)];
        }

        return [];
    }

    /// <summary>A part laid out on the bar: where it starts and how wide it is, in pixels.</summary>
    public readonly record struct Placed(double Left, double Width, Color Color);

    /// <summary>Smallest width a part keeps on its own; narrower ones merge into the last part.</summary>
    public const double MinSegmentWidth = 2;

    /// <summary>Lays <paramref name="segments"/> (in order, left to right) across <paramref
    /// name="filledWidth"/> pixels in proportion to their shares. A part narrower than <see
    /// cref="MinSegmentWidth"/> merges, with every narrower part before it, into the last part, which
    /// keeps its color. The widths add up to <paramref name="filledWidth"/>.</summary>
    public static IReadOnlyList<Placed> Layout(IReadOnlyList<BarSegment> segments, double filledWidth)
    {
        var shareSum = segments.Sum(segment => segment.SharePercent);
        if (segments.Count == 0 || filledWidth <= 0 || shareSum <= 0)
            return [];

        var widths = segments.Select(segment => filledWidth * segment.SharePercent / shareSum).ToList();
        var placed = new List<Placed>();
        var carried = 0.0;
        var left = 0.0;
        for (var i = 0; i < segments.Count; i++)
        {
            var isLast = i == segments.Count - 1;
            if (widths[i] < MinSegmentWidth && !isLast)
            {
                carried += widths[i];
                continue;
            }

            var width = isLast ? filledWidth - left : widths[i] + carried;
            carried = 0;
            placed.Add(new Placed(left, width, segments[i].Color));
            left += width;
        }

        return placed;
    }
}
