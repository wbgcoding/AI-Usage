using System.Globalization;
using AiUsage.Services;
using AiUsage.Stats.Pricing;

namespace AiUsage.Stats;

/// <summary>
/// What the statistics view model asks for the cost estimate: a figure or a tooltip line for any set of
/// usage rows, or nothing when the estimate is off, no prices are loaded yet or no service is attached.
/// One instance is made per rebuild, so every figure of a rebuild reads the same switch and the same
/// price list.
/// </summary>
internal sealed class StatsCostView(CostService? service)
{
    private readonly CostService? _service = service is { Enabled: true } enabled && !enabled.Prices.IsEmpty ? service : null;

    /// <summary>True when cost figures are shown at all.</summary>
    public bool Active => _service is not null;

    public CostSummary Sum(IEnumerable<StatsRecord> records) => _service?.Sum(records) ?? CostSummary.Empty;

    /// <summary>The cache-share tooltip: one line per part, "label: tokens" and, when the estimate is on,
    /// the part's cost after it (not for a part without tokens), then the line naming the models without a price. Empty when no tokens.</summary>
    public string SplitTooltip(IReadOnlyList<string> labels, IReadOnlyList<long> tokens, IEnumerable<StatsRecord> records)
    {
        if (tokens.Sum() <= 0)
            return "";

        var split = _service?.Split(records);
        var lines = PartLines(labels, tokens, split);
        if (split is { AnyPriced: true } && CostService.UnpricedLine(new CostSummary(0, true, split.UnpricedModels)) is { } unpriced)
            lines.Add(unpriced);
        return string.Join('\n', lines);
    }

    /// <summary>The tooltip of a total figure: <paramref name="totalLine"/>, the parts as in <see cref="SplitTooltip"/>,
    /// then the whole API cost and the line naming the models without a price. Empty when no tokens.</summary>
    public string TotalTooltip(string totalLine, IReadOnlyList<string> labels, IReadOnlyList<long> tokens, IEnumerable<StatsRecord> records)
    {
        if (tokens.Sum() <= 0)
            return "";

        var rows = records as IReadOnlyList<StatsRecord> ?? records.ToList();
        var lines = new List<string> { totalLine };
        lines.AddRange(PartLines(labels, tokens, _service?.Split(rows)));
        if (Line(rows) is { } cost)
            lines.Add(cost);
        return string.Join('\n', lines);
    }

    private List<string> PartLines(IReadOnlyList<string> labels, IReadOnlyList<long> tokens, CostSplit? split)
    {
        var lines = new List<string>(labels.Count + 2);
        for (var i = 0; i < labels.Count && i < tokens.Count; i++)
        {
            var line = labels[i] + ": " + tokens[i].ToString("N0", CultureInfo.CurrentCulture);
            if (split is { AnyPriced: true } && tokens[i] > 0)
                line += " \u00B7 " + Label(new CostSummary(split.AsParts[i], true, []));
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The figure for <paramref name="summary"/>, or empty.</summary>
    public string Text(CostSummary summary) => _service?.Text(summary) ?? "";

    public string Text(IEnumerable<StatsRecord> records) => _service is null ? "" : _service.Text(records);

    /// <summary>The tooltip lines for a figure: "API cost: ~2,00 €", then the line naming the models
    /// without a price. Null when there is nothing to say.</summary>
    public string? Line(CostSummary summary)
    {
        var text = Text(summary);
        if (text.Length == 0)
            return null;

        var line = LocalizationService.Instance["Stats.Cost.Column"] + " " + text;
        return CostService.UnpricedLine(summary) is { } unpriced ? line + "\n" + unpriced : line;
    }

    public string? Line(IEnumerable<StatsRecord> records) => _service is null ? null : Line(Sum(records));

    /// <summary>The figures of the whole set, one per bucket.</summary>
    public Dictionary<string, CostSummary> ByKey(
        IEnumerable<StatsRecord> records, Func<StatsRecord, string> keyOf, IEqualityComparer<string>? comparer = null)
    {
        var result = new Dictionary<string, CostSummary>(comparer ?? StringComparer.Ordinal);
        if (_service is null)
            return result;

        foreach (var bucket in records.GroupBy(keyOf, comparer ?? StringComparer.Ordinal))
            result[bucket.Key] = _service.Sum(bucket);
        return result;
    }

    /// <summary>The short in-place figure of <paramref name="summary"/>: no star, empty when nothing is priced.</summary>
    public string Label(CostSummary summary) => _service?.Label(summary) ?? "";

    /// <summary>The in-place figure of one bucket of <see cref="ByKey"/>, empty when unknown.</summary>
    public string LabelFor(Dictionary<string, CostSummary> byKey, string key) =>
        byKey.TryGetValue(key, out var summary) ? Label(summary) : "";

    /// <summary>The tooltip line of one bucket of <see cref="ByKey"/>, or null.</summary>
    public string? LineFor(Dictionary<string, CostSummary> byKey, string key) =>
        byKey.TryGetValue(key, out var summary) ? Line(summary) : null;

    /// <summary>The same figures, without the unpriced sentence: for a short suffix on a row.</summary>
    public string TextFor(Dictionary<string, CostSummary> byKey, string key) =>
        byKey.TryGetValue(key, out var summary) ? Text(summary) : "";

    /// <summary>The cost of the rows by token kind; empty when the estimate is off.</summary>
    public CostSplit Split(IEnumerable<StatsRecord> records) => _service?.Split(records) ?? CostSplit.Empty;

    /// <summary>The dollars, the amount in the shown currency and its code for the export; all empty when
    /// unknown or the estimate is off.</summary>
    public (string Usd, string Cost, string Currency) ForExport(CostSummary summary) =>
        _service?.ExportFigures(summary) ?? ("", "", "");

    /// <summary>A cost figure as plain dollars for the export, empty when unknown.</summary>
    public static string UsdForExport(CostSummary summary) => CostService.UsdForExport(summary);
}
