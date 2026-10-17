using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Stats.Pricing;

/// <summary>The cost of a set of usage rows: the dollar sum of the models that have a price and the
/// names of the models that have none (their tokens are not counted).</summary>
public sealed record CostSummary(double Usd, bool AnyPriced, IReadOnlyList<string> UnpricedModels)
{
    public static CostSummary Empty { get; } = new(0, false, []);

    public bool HasUnpriced => UnpricedModels.Count > 0;

    /// <summary>The sum of two summaries, with the unpriced names merged.</summary>
    public CostSummary Plus(CostSummary other) => new(
        Usd + other.Usd, AnyPriced || other.AnyPriced,
        UnpricedModels.Count == 0 ? other.UnpricedModels
        : other.UnpricedModels.Count == 0 ? UnpricedModels
        : [.. UnpricedModels.Union(other.UnpricedModels, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
}

/// <summary>The price arithmetic, a pure function of a price and token counts. The stored input count
/// of every provider is already the uncached share (the parsers subtract the cache read), so it is
/// priced as is.</summary>
public static class CostCalculator
{
    /// <summary>US dollars for one usage row. A cache price the list does not have falls back to the
    /// input price.</summary>
    public static double Cost(ModelPrice price, StatsRecord record) =>
        Math.Max(0, record.InputTokens) * price.Input
        + Math.Max(0, record.OutputTokens) * price.Output
        + Math.Max(0, record.CacheReadTokens) * (price.CacheRead ?? price.Input)
        + Math.Max(0, record.CacheCreationTokens) * (price.CacheCreation ?? price.Input);

    /// <summary>Sums the rows. Rows are grouped by model first so each model is looked up once.</summary>
    public static CostSummary Sum(IEnumerable<StatsRecord> records, ModelPriceList prices)
    {
        var byModel = new Dictionary<string, (long Input, long Output, long Read, long Creation)>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.TotalTokens <= 0)
                continue;
            byModel.TryGetValue(record.Model, out var sum);
            byModel[record.Model] = (
                sum.Input + Math.Max(0, record.InputTokens), sum.Output + Math.Max(0, record.OutputTokens),
                sum.Read + Math.Max(0, record.CacheReadTokens), sum.Creation + Math.Max(0, record.CacheCreationTokens));
        }

        var usd = 0.0;
        var anyPriced = false;
        List<string>? unpriced = null;
        foreach (var (model, sum) in byModel)
        {
            if (prices.Find(model) is not { } price)
            {
                (unpriced ??= []).Add(ReadChangeLog.SafeName(model));
                continue;
            }

            anyPriced = true;
            usd += sum.Input * price.Input + sum.Output * price.Output
                + sum.Read * (price.CacheRead ?? price.Input) + sum.Creation * (price.CacheCreation ?? price.Input);
        }

        unpriced?.Sort(StringComparer.Ordinal);
        return new CostSummary(usd, anyPriced, unpriced ?? []);
    }
}

/// <summary>Turns a dollar figure into the text shown next to the tokens.</summary>
public static class CostFormatter
{
    /// <summary>The currency in effect: euro when asked for, or on <see cref="CostCurrency.Auto"/> when
    /// the region's currency is the euro. Never returns <see cref="CostCurrency.Auto"/>.</summary>
    public static CostCurrency Effective(CostCurrency setting, RegionInfo region) => setting switch
    {
        CostCurrency.Eur => CostCurrency.Eur,
        CostCurrency.Auto when string.Equals(region.ISOCurrencySymbol, "EUR", StringComparison.OrdinalIgnoreCase) => CostCurrency.Eur,
        _ => CostCurrency.Usd,
    };

    /// <summary>"~12,34 $" or "~12,34 €" in the currency format of <paramref name="culture"/> (position
    /// and separators of that culture, the symbol of the shown currency); under a cent it shows the
    /// cent. Euro needs <paramref name="usdPerEur"/>; without a rate the dollar figure shows.</summary>
    public static string Format(double usd, CostCurrency effective, double? usdPerEur, CultureInfo culture)
    {
        var euro = effective == CostCurrency.Eur && usdPerEur is > 0;
        var value = euro ? usd / usdPerEur!.Value : usd;
        var numbers = (NumberFormatInfo)culture.NumberFormat.Clone();
        numbers.CurrencySymbol = euro ? "€" : "$";
        // A no-break space keeps the figure and its symbol on one line.
        return "~" + Math.Max(value, 0.01).ToString("C2", numbers).Replace(' ', ' ');
    }

    /// <summary>The figure for a summary: the priced part, with a trailing <c>*</c> when some models
    /// have no price; "?*" when none has.</summary>
    public static string Format(CostSummary summary, CostCurrency effective, double? usdPerEur, CultureInfo culture)
    {
        if (!summary.AnyPriced)
            return summary.HasUnpriced ? "?*" : "";
        var text = Format(summary.Usd, effective, usdPerEur, culture);
        return summary.HasUnpriced ? text + "*" : text;
    }
}
