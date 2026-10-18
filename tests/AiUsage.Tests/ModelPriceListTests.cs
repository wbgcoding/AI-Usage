using AiUsage.Services;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

public class ModelPriceListTests
{
    private const string Sample = """
    {
      "sample_spec": { "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000002, "mode": "chat" },
      "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "cache_read_input_token_cost": 0.0000003, "cache_creation_input_token_cost": 0.00000375, "litellm_provider": "anthropic", "mode": "chat" },
      "claude-opus-4": { "input_cost_per_token": 0.000015, "output_cost_per_token": 0.000075, "litellm_provider": "anthropic", "mode": "chat" },
      "anthropic/claude-haiku-4-5": { "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000005, "mode": "chat" },
      "gpt-5": { "input_cost_per_token": 0.00000125, "output_cost_per_token": 0.00001, "cache_read_input_token_cost": 0.000000125, "mode": "responses" },
      "openai/gpt-5-mini": { "input_cost_per_token": 0.00000025, "output_cost_per_token": 0.000002 },
      "gemini/gemini-2.5-pro": { "input_cost_per_token": 0.00000125, "output_cost_per_token": 0.00001, "mode": "chat" },
      "text-embedding-3": { "input_cost_per_token": 0.0000001, "output_cost_per_token": 0, "mode": "embedding" },
      "negative-model": { "input_cost_per_token": -0.001, "output_cost_per_token": 0.000002, "mode": "chat" },
      "huge-model": { "input_cost_per_token": 0.5, "output_cost_per_token": 0.000002, "mode": "chat" },
      "text-model": { "input_cost_per_token": "cheap", "output_cost_per_token": 0.000002, "mode": "chat" },
      "bad-cache-model": { "input_cost_per_token": 0.000002, "output_cost_per_token": 0.000004, "cache_read_input_token_cost": 5, "cache_creation_input_token_cost": -1, "mode": "chat" },
      "not-an-object": 5,
      "list-model": [1, 2]
    }
    """;

    private static string TempDirectory() => TestPaths.GetPath("ai-usage-prices");

    private static ModelPriceList Loaded(string json = Sample)
    {
        var directory = TempDirectory();
        var list = new ModelPriceList(() => directory);
        list.Replace(ModelPriceList.Parse(json), DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    [Fact]
    public void Snapshot_keeps_its_prices_when_the_list_is_replaced()
    {
        var list = Loaded();
        var snapshot = list.Snapshot();

        list.Replace(ModelPriceList.Parse("""{ "other": { "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000002 } }"""),
            DateTimeOffset.UtcNow, persist: false);

        Assert.NotNull(snapshot.Find("claude-sonnet-4-5"));
        Assert.Null(snapshot.Find("other"));
        Assert.Null(list.Find("claude-sonnet-4-5"));
        Assert.False(snapshot.IsEmpty);
    }

    [Fact]
    public void Parse_keeps_chat_responses_and_unmarked_models_only()
    {
        var parsed = ModelPriceList.Parse(Sample);

        Assert.Contains("claude-sonnet-4-5", parsed.Keys);
        Assert.Contains("gpt-5", parsed.Keys);
        Assert.Contains("openai/gpt-5-mini", parsed.Keys);
        Assert.DoesNotContain("text-embedding-3", parsed.Keys);
        Assert.DoesNotContain("sample_spec", parsed.Keys);
        Assert.DoesNotContain("not-an-object", parsed.Keys);
        Assert.DoesNotContain("list-model", parsed.Keys);
    }

    [Fact]
    public void Parse_drops_planted_bad_prices()
    {
        var parsed = ModelPriceList.Parse(Sample);

        Assert.DoesNotContain("negative-model", parsed.Keys);
        Assert.DoesNotContain("huge-model", parsed.Keys);
        Assert.DoesNotContain("text-model", parsed.Keys);

        // A bad cache field only loses that field; the model stays priced.
        var cache = parsed["bad-cache-model"];
        Assert.Equal(0.000002, cache.Input);
        Assert.Equal(0.000004, cache.Output);
        Assert.Null(cache.CacheRead);
        Assert.Null(cache.CacheCreation);
    }

    [Fact]
    public void Parse_reads_all_four_prices()
    {
        var price = ModelPriceList.Parse(Sample)["claude-sonnet-4-5"];

        Assert.Equal(0.000003, price.Input);
        Assert.Equal(0.000015, price.Output);
        Assert.Equal(0.0000003, price.CacheRead);
        Assert.Equal(0.00000375, price.CacheCreation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"a\":")]
    public void Parse_of_garbage_is_empty_and_never_throws(string json) =>
        Assert.Empty(ModelPriceList.Parse(json));

    [Fact]
    public void Find_prefers_the_exact_key()
    {
        var list = Loaded();
        Assert.Equal(0.000003, list.Find("claude-sonnet-4-5")!.Input);
        Assert.Equal(0.000003, list.Find("Claude-Sonnet-4-5")!.Input);
    }

    [Theory]
    [InlineData("claude-haiku-4-5", 0.000001)]
    [InlineData("gpt-5-mini", 0.00000025)]
    [InlineData("gemini-2.5-pro", 0.00000125)]
    public void Find_tries_the_maker_prefixes(string model, double input) =>
        Assert.Equal(input, Loaded().Find(model)!.Input);

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("claude-sonnet-4-5-2025-09-29")]
    public void Find_drops_a_date_suffix(string model) =>
        Assert.Equal(0.000003, Loaded().Find(model)!.Input);

    [Fact]
    public void Find_takes_the_longest_key_that_is_a_prefix_of_the_model()
    {
        var list = Loaded();

        Assert.Equal(0.000015, list.Find("claude-opus-4-1")!.Input);
        Assert.Equal(0.00000125, list.Find("gpt-5-codex")!.Input);
    }

    [Fact]
    public void Find_never_crosses_to_another_maker()
    {
        Assert.Null(Loaded().Find("gemini-opus-9"));
        Assert.Null(Loaded().Find("llama-3"));
    }

    [Fact]
    public void Find_of_an_unknown_model_is_null_and_reported_once()
    {
        var list = Loaded();
        using var capture = ReadChangeLog.BeginCapture();

        Assert.Null(list.Find("mystery-model-9"));
        Assert.Null(list.Find("mystery-model-9"));
        Assert.Null(list.Find("mystery-model-9"));

        var line = Assert.Single(capture.Lines);
        Assert.Contains("CHANGE DETECTED", line);
        Assert.Contains("Pricing", line);
        Assert.Contains("mystery-model-9", line);
        Assert.Contains("left out of the cost estimate", line);
    }

    [Fact]
    public void Find_on_an_empty_list_reports_nothing_and_remembers_nothing()
    {
        var directory = TempDirectory();
        var list = new ModelPriceList(() => directory);
        using var capture = ReadChangeLog.BeginCapture();

        Assert.Null(list.Find("claude-sonnet-4-5"));
        Assert.Empty(capture.Lines);

        list.Replace(ModelPriceList.Parse(Sample), DateTimeOffset.UtcNow, persist: false);
        Assert.NotNull(list.Find("claude-sonnet-4-5"));
    }

    [Fact]
    public void The_trimmed_cache_round_trips_and_keeps_the_fetch_time()
    {
        var directory = TempDirectory();
        try
        {
            var stamp = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
            var writer = new ModelPriceList(() => directory);
            writer.Replace(ModelPriceList.Parse(Sample), stamp, persist: true);

            var reader = new ModelPriceList(() => directory);
            reader.Load();

            Assert.Equal(stamp, reader.FetchedAt);
            Assert.Equal(0.0000003, reader.Find("claude-sonnet-4-5")!.CacheRead);
            Assert.Null(reader.Find("gpt-5")!.CacheCreation);
            Assert.DoesNotContain("litellm", File.ReadAllText(Path.Combine(directory, "model-prices.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_corrupt_cache_file_loads_as_empty()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "model-prices.json"), "{ broken");
            var list = new ModelPriceList(() => directory);
            list.Load();

            Assert.True(list.IsEmpty);
            Assert.Null(list.FetchedAt);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void IsStale_after_a_day()
    {
        var list = Loaded();
        var fetched = list.FetchedAt!.Value;

        Assert.False(list.IsStale(fetched.AddHours(23)));
        Assert.True(list.IsStale(fetched.AddHours(24)));
        Assert.True(new ModelPriceList(() => TestPaths.Root).IsStale(fetched));
    }
}
