using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

public class ModelPriceLookupTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    private static string TempDirectory() => TestPaths.GetPath("ai-usage-price-lookup");

    private static ModelPriceList ListOf(params string[] keys)
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Length; i++)
            entries[keys[i]] = new ModelPrice(0.000001 * (i + 1), 0.000002 * (i + 1), null, null);
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    private static StatsRecord Row(string model) => new("opencode", Day, model, "p", 1000, 1000, 0, 0);

    // ---- free models ----

    [Theory]
    [InlineData("nemotron-3-ultra-free")]
    [InlineData("inclusionai/ling-3.0-flash-vl:free")]
    [InlineData("x/y:free")]
    [InlineData("Free")]
    [InlineData("model_FREE_v2")]
    [InlineData("free-model")]
    public void A_model_with_free_as_its_own_word_costs_nothing(string model)
    {
        var price = ListOf("gpt-5").Find(model);
        Assert.NotNull(price);
        Assert.Equal(0, price.Input);
        Assert.Equal(0, price.Output);
    }

    [Theory]
    [InlineData("freedom-1")]
    [InlineData("carefree-x")]
    [InlineData("gpt-free5")]
    public void Free_inside_a_longer_word_is_not_free(string model) =>
        Assert.Null(ListOf("gpt-5").Find(model));

    [Fact]
    public void A_free_model_is_neither_unpriced_nor_starred()
    {
        var summary = CostCalculator.Sum([Row("nemotron-3-ultra-free"), Row("gpt-5")], ListOf("gpt-5"));
        Assert.True(summary.AnyPriced);
        Assert.False(summary.HasUnpriced);
        Assert.Equal(1000 * 0.000001 + 1000 * 0.000002, summary.Usd, 9);

        var onlyFree = CostCalculator.Sum([Row("x/y:free")], ListOf("gpt-5"));
        Assert.True(onlyFree.AnyPriced);
        Assert.False(onlyFree.HasUnpriced);
        Assert.Equal(0, onlyFree.Usd);
    }

    [Fact]
    public void An_empty_list_still_estimates_nothing_even_for_free_models()
    {
        var empty = new ModelPriceList(TempDirectory);
        Assert.Null(empty.Find("nemotron-3-ultra-free"));
        var summary = CostCalculator.Sum([Row("nemotron-3-ultra-free")], empty);
        Assert.False(summary.AnyPriced);
    }
}
