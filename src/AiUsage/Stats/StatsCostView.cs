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

    /// <summary>The tooltip line of one bucket of <see cref="ByKey"/>, or null.</summary>
    public string? LineFor(Dictionary<string, CostSummary> byKey, string key) =>
        byKey.TryGetValue(key, out var summary) ? Line(summary) : null;

    /// <summary>The same figures, without the unpriced sentence: for a short suffix on a row.</summary>
    public string TextFor(Dictionary<string, CostSummary> byKey, string key) =>
        byKey.TryGetValue(key, out var summary) ? Text(summary) : "";

    /// <summary>A cost figure as plain dollars for the export, empty when unknown.</summary>
    public static string UsdForExport(CostSummary summary) => CostService.UsdForExport(summary);
}
