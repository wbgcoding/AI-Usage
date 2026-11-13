using System.Globalization;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

/// <summary>The cost inputs frozen at one moment: sums, splits, texts and export figures all read the
/// same price table, currency, rate and culture.</summary>
public class CostSnapshotTests
{
    private const string PriceJson = """
    {
      "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "cache_read_input_token_cost": 0.0000003, "cache_creation_input_token_cost": 0.00000375, "mode": "chat" }
    }
    """;

    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static StatsRecord Record(string model, long input = 0, long output = 0, long create = 0, long read = 0) =>
        new("claude", Day, model, "p", input, output, create, read);

    private static ModelPriceList Prices(string? json = PriceJson)
    {
        var list = new ModelPriceList(() => TestPaths.Root);
        if (json is not null)
            list.Replace(ModelPriceList.Parse(json), DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    private static CostSnapshot Snapshot(CostCurrency currency = CostCurrency.Usd, double? rate = null, ModelPriceList? prices = null) =>
        new(prices ?? Prices(), currency, rate, En);

    [Theory]
    [InlineData(CostCurrency.Eur, 1.25, true)]
    [InlineData(CostCurrency.Eur, 0.0, false)]
    [InlineData(CostCurrency.Eur, -1.0, false)]
    [InlineData(CostCurrency.Eur, null, false)]
    [InlineData(CostCurrency.Usd, 1.25, false)]
    [InlineData(CostCurrency.Auto, 1.25, false)]
    public void Euro_needs_the_euro_currency_and_a_positive_rate(CostCurrency currency, double? rate, bool expected) =>
        Assert.Equal(expected, Snapshot(currency, rate).Euro);

    [Fact]
    public void The_rate_is_passed_through()
    {
        Assert.Equal(1.1, Snapshot(CostCurrency.Eur, 1.1).UsdPerEur);
        Assert.Null(Snapshot().UsdPerEur);
    }

    [Fact]
    public void Sum_prices_every_token_kind()
    {
        var summary = Snapshot().Sum([Record("claude-sonnet-4-5", input: 1_000_000, output: 1_000_000, create: 1_000_000, read: 1_000_000)]);

        Assert.True(summary.AnyPriced);
        Assert.Equal(3 + 15 + 3.75 + 0.3, summary.Usd, 6);
        Assert.Empty(summary.UnpricedModels);
    }

    [Fact]
    public void Sum_names_models_without_a_price_and_leaves_their_tokens_out()
    {
        var summary = Snapshot().Sum([Record("claude-sonnet-4-5", input: 1_000_000), Record("mystery-model", input: 5)]);

        Assert.Equal(3, summary.Usd, 6);
        Assert.Equal(["mystery-model"], summary.UnpricedModels);
    }

    [Fact]
    public void Sum_of_no_rows_is_unpriced_and_empty()
    {
        var summary = Snapshot().Sum([]);

        Assert.False(summary.AnyPriced);
        Assert.Equal(0, summary.Usd);
        Assert.Empty(summary.UnpricedModels);
    }

    [Fact]
    public void Split_keeps_the_four_token_kinds_apart()
    {
        var split = Snapshot().Split([Record("claude-sonnet-4-5", input: 1_000_000, output: 1_000_000, create: 1_000_000, read: 1_000_000)]);

        Assert.Equal(3, split.NewInput, 6);
        Assert.Equal(3.75, split.CacheWrite, 6);
        Assert.Equal(0.3, split.CacheRead, 6);
        Assert.Equal(15, split.Output, 6);
        Assert.True(split.AnyPriced);
    }

    [Fact]
    public void Text_formats_a_summary_in_dollars_with_a_star_for_unpriced_models()
    {
        var snapshot = Snapshot();

        Assert.Equal("~$3.00", snapshot.Text(new CostSummary(3, true, [])));
        Assert.Equal("~$3.00*", snapshot.Text(new CostSummary(3, true, ["x"])));
        Assert.Equal("?*", snapshot.Text(new CostSummary(0, false, ["x"])));
        Assert.Equal("", snapshot.Text(CostSummary.Empty));
    }

    [Fact]
    public void Text_of_rows_sums_then_formats()
    {
        Assert.Equal("~$3.00", Snapshot().Text([Record("claude-sonnet-4-5", input: 1_000_000)]));
    }

    [Fact]
    public void Text_shows_euros_when_a_rate_is_known_and_dollars_when_not()
    {
        var summary = new CostSummary(3, true, []);

        Assert.Equal("~€1.50", Snapshot(CostCurrency.Eur, 2.0).Text(summary));
        Assert.Equal("~$3.00", Snapshot(CostCurrency.Eur, null).Text(summary));
    }

    [Fact]
    public void Label_has_no_star_and_is_empty_when_nothing_is_priced()
    {
        var snapshot = Snapshot();

        Assert.Equal("~$3.00", snapshot.Label(new CostSummary(3, true, ["x"])));
        Assert.Equal("", snapshot.Label(new CostSummary(0, false, ["x"])));
        Assert.Equal("", snapshot.Label(CostSummary.Empty));
    }

    [Fact]
    public void A_free_model_shows_zero_and_a_tiny_cost_shows_one_cent()
    {
        var snapshot = Snapshot();

        Assert.Equal("~$0.00", snapshot.Label(new CostSummary(0, true, [])));
        Assert.Equal("~$0.01", snapshot.Label(new CostSummary(0.0001, true, [])));
    }

    [Fact]
    public void A_hundred_dollars_or_more_shows_without_cents()
    {
        Assert.Equal("~$150", Snapshot().Label(new CostSummary(150, true, [])));
    }

    [Fact]
    public void Export_figures_are_dollars_in_dollar_mode()
    {
        Assert.Equal(("3", "3", "USD"), Snapshot().ExportFigures(new CostSummary(3, true, [])));
    }

    [Fact]
    public void Export_figures_convert_to_euros_when_a_rate_is_known()
    {
        Assert.Equal(("3", "1.5", "EUR"), Snapshot(CostCurrency.Eur, 2.0).ExportFigures(new CostSummary(3, true, [])));
    }

    [Fact]
    public void Export_figures_are_empty_without_a_price()
    {
        Assert.Equal(("", "", ""), Snapshot().ExportFigures(CostSummary.Empty));
    }

    [Fact]
    public void A_later_change_of_the_live_list_does_not_change_a_taken_snapshot()
    {
        var live = Prices();
        var snapshot = Snapshot(prices: live.Snapshot());

        live.Replace(ModelPriceList.Parse("{}"), DateTimeOffset.UtcNow, persist: false);

        Assert.True(snapshot.Sum([Record("claude-sonnet-4-5", input: 1)]).AnyPriced);
        Assert.Null(live.Snapshot().Find("claude-sonnet-4-5"));
    }

    [Fact]
    public void The_culture_decides_the_number_format()
    {
        var german = new CostSnapshot(Prices(), CostCurrency.Usd, null, CultureInfo.GetCultureInfo("de-DE"));

        var text = german.Label(new CostSummary(1234, true, []));

        Assert.Contains("1.234", text);
        Assert.Contains("$", text);
    }
}
