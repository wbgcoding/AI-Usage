using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Stats.Pricing;

/// <summary>
/// The cost inputs as they stood at one moment: switch on, price table, currency, rate and culture,
/// all fixed. Work on another thread reads this instead of the live service, so a settings change or a
/// price download meanwhile cannot change a figure half way through.
/// </summary>
internal sealed class CostSnapshot(ModelPriceList prices, CostCurrency currency, double? usdPerEur, CultureInfo culture)
{
    /// <summary>True when figures show in euros (a rate is known); else they show in dollars.</summary>
    public bool Euro => currency == CostCurrency.Eur && usdPerEur is > 0;

    public double? UsdPerEur => usdPerEur;

    public CostSummary Sum(IEnumerable<StatsRecord> records) => CostCalculator.Sum(records, prices);

    public CostSplit Split(IEnumerable<StatsRecord> records) => CostCalculator.Split(records, prices);

    public string Text(CostSummary summary) => CostFormatter.Format(summary, currency, usdPerEur, culture);

    public string Text(IEnumerable<StatsRecord> records) => Text(Sum(records));

    public string Label(CostSummary summary) =>
        summary.AnyPriced ? CostFormatter.Format(summary.Usd, currency, usdPerEur, culture) : "";

    public (string Usd, string Cost, string Currency) ExportFigures(CostSummary summary) =>
        CostService.ExportFigures(summary, currency, usdPerEur);
}
