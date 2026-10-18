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

    // ---- equal names ----

    [Fact]
    public void A_key_whose_bare_name_equals_the_model_is_found()
    {
        var list = ListOf("minimax/MiniMax-M3");
        Assert.NotNull(list.Find("minimax-m3"));
        Assert.NotNull(list.Find("MiniMax-M3"));
    }

    [Fact]
    public void Equal_names_prefer_the_models_own_maker()
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["fireworks_ai/minimax-m3"] = new(0.000001, 0.000002, null, null),
            ["minimax/MiniMax-M3"] = new(0.000003, 0.000004, null, null),
            ["azure_ai/minimax-m3"] = new(0.000005, 0.000006, null, null),
        };
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        Assert.Equal(0.000003, list.Find("minimax-m3")!.Input);
    }

    [Fact]
    public void Equal_names_without_the_own_maker_take_the_shortest_key_then_ordinal_order()
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["fireworks_ai/minimax-m3"] = new(0.000001, 0.000002, null, null),
            ["az/minimax-m3"] = new(0.000003, 0.000004, null, null),
            ["zz/minimax-m3"] = new(0.000005, 0.000006, null, null),
        };
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        Assert.Equal(0.000003, list.Find("minimax-m3")!.Input);
    }

    [Fact]
    public void An_equal_name_beats_a_longer_key_and_stays_inside_its_maker_family()
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["minimax/MiniMax-M3-highspeed"] = new(0.000009, 0.000009, null, null),
            ["fireworks_ai/minimax-m3"] = new(0.000001, 0.000002, null, null),
            ["other/minimax"] = new(0.000007, 0.000007, null, null),
        };
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        Assert.Equal(0.000001, list.Find("minimax-m3")!.Input);
    }

    // ---- alias suffixes ----

    private static ModelPriceList GeminiList()
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini/gemini-pro-latest"] = new(0.000001, 0.000002, null, null),
            ["gemini-pro-latest"] = new(0.000001, 0.000002, null, null),
            ["gemini/gemini-3-flash-preview"] = new(0.000003, 0.000004, null, null),
            ["gemini-3-flash-preview"] = new(0.000003, 0.000004, null, null),
            ["minimax/MiniMax-M3"] = new(0.000005, 0.000006, null, null),
        };
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    [Theory]
    [InlineData("gemini-pro-default", 0.000001)]
    [InlineData("gemini-pro-d", 0.000001)]
    [InlineData("gemini-3-flash-a", 0.000003)]
    [InlineData("Gemini-Pro-Default", 0.000001)]
    public void A_tool_alias_finds_the_plain_model(string alias, double input) =>
        Assert.Equal(input, GeminiList().Find(alias)!.Input);

    [Fact]
    public void A_real_key_wins_over_the_alias_retry()
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-pro-default"] = new(0.000009, 0.000009, null, null),
            ["gemini-pro-latest"] = new(0.000001, 0.000001, null, null),
        };
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        Assert.Equal(0.000009, list.Find("gemini-pro-default")!.Input);
    }

    [Fact]
    public void An_alias_without_a_plain_model_stays_unknown()
    {
        var list = GeminiList();
        Assert.Null(list.Find("codex-auto-review"));
        Assert.Null(list.Find("big-pickle-default"));
        Assert.Null(list.Find("-d"));
    }
}
