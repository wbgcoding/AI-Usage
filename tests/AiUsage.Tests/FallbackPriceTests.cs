using System.Globalization;
using System.Net;
using System.Net.Http;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

public class FallbackPriceTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    private static readonly string Fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "models-dev-sample.json"));

    private const string PriceJson = """
    {
      "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "mode": "chat" }
    }
    """;

    private static string TempDirectory() => TestPaths.GetPath("ai-usage-fallback");

    private static FallbackPriceList Loaded()
    {
        var list = new FallbackPriceList(TempDirectory);
        list.Replace(FallbackPriceList.Parse(Fixture), DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    private static StatsRecord Row(string model) => new("opencode", Day, model, "p", 1000, 1000, 0, 0);

    // ---- table ----

    [Fact]
    public void The_sample_table_is_read_with_prices_per_token()
    {
        var entries = FallbackPriceList.Parse(Fixture);

        Assert.Equal(6, entries.Count);
        var nano = entries.Single(entry => entry.Provider == "nano-gpt" && entry.Model == "minimax/minimax-m3");
        Assert.Equal(0.23e-6, nano.Price.Input, 12);
        Assert.Equal(0.96e-6, nano.Price.Output, 12);
        Assert.Equal(0.05e-6, nano.Price.CacheRead!.Value, 12);
        Assert.Null(nano.Price.CacheCreation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":3}")]
    [InlineData("{\"p\":{\"models\":{\"m\":{\"cost\":{\"input\":\"x\",\"output\":1}}}}}")]
    [InlineData("{\"p\":{\"models\":{\"m\":{\"cost\":{\"input\":999999999,\"output\":1}}}}}")]
    [InlineData("{\"p\":{\"models\":{\"m\":{\"cost\":{\"input\":-1,\"output\":1}}}}}")]
    [InlineData("{\"p\":{\"models\":{\"m\":{\"cost\":{\"input\":1}}}}}")]
    public void Text_that_is_not_a_usable_table_gives_no_entries(string json) =>
        Assert.Empty(FallbackPriceList.Parse(json));

    [Fact]
    public void A_price_beyond_the_sane_range_drops_only_that_field()
    {
        var entries = FallbackPriceList.Parse(
            "{\"p\":{\"models\":{\"m\":{\"cost\":{\"input\":1,\"output\":2,\"cache_read\":99999999}}}}}");

        var entry = Assert.Single(entries);
        Assert.Null(entry.Price.CacheRead);
    }

    // ---- matching ----

    [Fact]
    public void A_model_that_costs_nothing_everywhere_is_free()
    {
        var price = Loaded().Find("big-pickle");

        Assert.NotNull(price);
        Assert.Equal(0, price.Input);
        Assert.Equal(0, price.Output);
    }

    [Fact]
    public void The_last_name_segment_matches_a_provider_prefixed_name()
    {
        Assert.NotNull(Loaded().Find("opencode-zen/big-pickle"));
        Assert.NotNull(Loaded().Find("Big-Pickle"));
    }

    [Fact]
    public void Free_entries_are_ignored_while_a_paid_one_exists_and_the_own_maker_wins()
    {
        var price = Loaded().Find("minimax-m3");

        Assert.NotNull(price);
        Assert.Equal(0.23e-6, price.Input, 12);
    }

    [Fact]
    public void Without_an_own_maker_the_median_of_the_paid_matches_is_taken()
    {
        var list = new FallbackPriceList(TempDirectory);
        list.Replace(
            [
                new FallbackPriceList.Entry("a", "m", new ModelPrice(3e-6, 1e-6, null, null)),
                new FallbackPriceList.Entry("b", "m", new ModelPrice(1e-6, 1e-6, null, null)),
                new FallbackPriceList.Entry("c", "m", new ModelPrice(2e-6, 1e-6, null, null)),
                new FallbackPriceList.Entry("d", "m", new ModelPrice(0, 0, null, null)),
            ],
            DateTimeOffset.UtcNow, persist: false);

        Assert.Equal(2e-6, list.Find("m")!.Input, 12);
    }

    [Fact]
    public void A_date_suffix_is_tried_only_after_the_plain_name_found_nothing()
    {
        Assert.NotNull(Loaded().Find("big-pickle-20260101"));
        Assert.Null(Loaded().Find("something-else"));
        Assert.Null(new FallbackPriceList(TempDirectory).Find("big-pickle"));
    }

    [Fact]
    public void The_table_is_cached_and_read_back()
    {
        var directory = TempDirectory();
        var stamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        new FallbackPriceList(() => directory).Replace(FallbackPriceList.Parse(Fixture), stamp);

        var again = new FallbackPriceList(() => directory);
        again.Load();

        Assert.Equal(stamp, again.FetchedAt);
        Assert.Equal(0.23e-6, again.Find("minimax-m3")!.Input, 12);
        Assert.Equal(0.4e-6, again.Find("nvidia/nemotron-3-ultra-550b-a55b")!.Input, 12);
    }

    [Fact]
    public void A_damaged_cache_file_leaves_the_table_empty()
    {
        var directory = TempDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FallbackPriceList.CacheFileName), "{not json");

        var list = new FallbackPriceList(() => directory);
        list.Load();

        Assert.True(list.IsEmpty);
    }

    // ---- through the main list ----

    private static ModelPriceList MainWith(params string[] keys)
    {
        var entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
            entries[key] = new ModelPrice(1e-6, 2e-6, null, null);
        var list = new ModelPriceList(TempDirectory);
        list.Replace(entries, DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    [Fact]
    public void The_main_list_asks_the_second_table_only_for_a_miss()
    {
        var list = MainWith("gpt-5");
        list.Fallback.Replace(FallbackPriceList.Parse(Fixture), DateTimeOffset.UtcNow, persist: false);
        var wanted = 0;
        list.Fallback.WantedRaised += () => wanted++;

        Assert.NotNull(list.Find("gpt-5"));
        Assert.Equal(0, wanted);
        Assert.False(list.Fallback.Wanted);

        Assert.NotNull(list.Find("big-pickle"));
        Assert.Equal(1, wanted);
        Assert.True(list.Fallback.Wanted);
    }

    [Fact]
    public void The_real_unpriced_names_are_priced_except_the_one_nobody_lists()
    {
        var list = MainWith(
            "minimax/MiniMax-M3", "fireworks_ai/minimax-m3", "gemini/gemini-3-flash-preview", "gemini-3-flash-preview",
            "gemini/gemini-pro-latest", "gemini-pro-latest");
        list.ReplaceFallback(FallbackPriceList.Parse(Fixture), DateTimeOffset.UtcNow, persist: false);

        string[] priced =
        [
            "gemini-pro-default", "big-pickle", "minimax-m3", "gemini-3-flash-a", "gemini-pro-d",
            "inclusionai/ling-3.0-flash-vl:free", "nemotron-3-ultra-free", "opencode-zen/big-pickle",
        ];
        foreach (var model in priced)
            Assert.True(list.Find(model) is not null, model);
        Assert.Null(list.Find("codex-auto-review"));
    }

    [Fact]
    public void A_model_the_second_table_prices_is_not_starred()
    {
        var list = MainWith("gpt-5");
        list.ReplaceFallback(FallbackPriceList.Parse(Fixture), DateTimeOffset.UtcNow, persist: false);

        var summary = CostCalculator.Sum([Row("big-pickle"), Row("gpt-5"), Row("codex-auto-review")], list);

        Assert.Equal(["codex-auto-review"], summary.UnpricedModels);
        Assert.True(summary.AnyPriced);
    }

    // ---- service ----

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage ForUrl(Uri uri) => uri.Host switch
    {
        "raw.githubusercontent.com" => Ok(PriceJson),
        "models.dev" => Ok(Fixture),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static CostService Service(
        FakeHandler handler, string directory, bool enabled = true, bool networkAllowed = true, Func<DateTimeOffset>? now = null) =>
        new(() => enabled, () => CostCurrency.Usd, directory, () => CultureInfo.GetCultureInfo("en-US"), () => new RegionInfo("US"),
            now ?? (() => DateTimeOffset.UtcNow), () => networkAllowed, handler);

    private static bool AskedModelsDev(FakeHandler handler)
    {
        lock (handler.Requests)
            return handler.Requests.Any(uri => uri.Host == "models.dev");
    }

    [Fact]
    public async Task A_miss_loads_the_second_table_once_and_raises_changed()
    {
        var handler = new FakeHandler(ForUrl);
        var directory = TempDirectory();
        var service = Service(handler, directory);
        await service.RefreshAsync();
        Assert.False(AskedModelsDev(handler));

        var changed = 0;
        service.Changed += () => changed++;
        var before = service.Sum([Row("big-pickle")]);
        Assert.False(before.AnyPriced);
        await service.FallbackTask;

        Assert.True(AskedModelsDev(handler));
        Assert.Equal(1, changed);
        Assert.True(File.Exists(Path.Combine(directory, FallbackPriceList.CacheFileName)));
        var after = service.Sum([Row("big-pickle")]);
        Assert.True(after.AnyPriced);
        Assert.False(after.HasUnpriced);
        Assert.Empty(Directory.GetFiles(directory, "download-*"));
    }

    [Fact]
    public async Task Models_the_main_list_knows_never_cause_the_second_download()
    {
        var handler = new FakeHandler(ForUrl);
        var service = Service(handler, TempDirectory());
        await service.RefreshAsync();

        service.Sum([Row("claude-sonnet-4-5"), Row("nemotron-3-ultra-free")]);
        await service.FallbackTask;
        await service.RefreshAsync();

        Assert.False(AskedModelsDev(handler));
    }

    [Fact]
    public async Task The_second_download_waits_for_the_setting_and_the_network()
    {
        var handler = new FakeHandler(ForUrl);
        var directory = TempDirectory();
        await Service(handler, directory).RefreshAsync();

        var held = Service(handler, directory, networkAllowed: false);
        held.Sum([Row("big-pickle")]);
        await held.FallbackTask;
        await held.RefreshAsync();
        Assert.False(AskedModelsDev(handler));

        var off = Service(handler, directory, enabled: false);
        off.Prices.Find("big-pickle");
        await off.FallbackTask;
        await off.RefreshAsync();
        Assert.False(AskedModelsDev(handler));
    }

    [Fact]
    public async Task A_fresh_second_cache_is_not_downloaded_again_and_a_failure_waits_an_hour()
    {
        var directory = TempDirectory();
        var first = new FakeHandler(ForUrl);
        var firstService = Service(first, directory);
        await firstService.RefreshAsync();
        firstService.Sum([Row("big-pickle")]);
        await firstService.FallbackTask;
        Assert.True(AskedModelsDev(first));

        var second = new FakeHandler(ForUrl);
        var secondService = Service(second, directory);
        Assert.True(secondService.Sum([Row("big-pickle")]).AnyPriced);
        await secondService.FallbackTask;
        Assert.False(AskedModelsDev(second));

        var clock = DateTimeOffset.UtcNow;
        var failing = new FakeHandler(uri => uri.Host == "models.dev" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : ForUrl(uri));
        var stale = Service(failing, TempDirectory(), now: () => clock);
        await stale.RefreshAsync();
        stale.Sum([Row("big-pickle")]);
        await stale.FallbackTask;
        var asked = failing.Requests.Count(uri => uri.Host == "models.dev");
        Assert.Equal(1, asked);

        clock = clock.AddMinutes(30);
        await stale.RefreshFallbackAsync();
        Assert.Equal(asked, failing.Requests.Count(uri => uri.Host == "models.dev"));

        clock = clock.AddHours(1);
        await stale.RefreshFallbackAsync();
        Assert.Equal(asked + 1, failing.Requests.Count(uri => uri.Host == "models.dev"));
    }

    [Fact]
    public async Task A_body_without_usable_prices_does_not_replace_the_second_table()
    {
        var handler = new FakeHandler(uri => uri.Host == "models.dev" ? Ok("[1,2,3]") : ForUrl(uri));
        var service = Service(handler, TempDirectory());
        await service.RefreshAsync();

        service.Sum([Row("big-pickle")]);
        await service.FallbackTask;

        Assert.True(service.Prices.Fallback.IsEmpty);
    }

    [Fact]
    public async Task A_redirect_away_from_the_one_host_is_refused()
    {
        var handler = new FakeHandler(uri => uri.Host == "models.dev"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/api.json") } }
            : uri.Host == "evil.example" ? Ok(Fixture) : ForUrl(uri));
        var service = Service(handler, TempDirectory());
        await service.RefreshAsync();

        service.Sum([Row("big-pickle")]);
        await service.FallbackTask;

        Assert.True(service.Prices.Fallback.IsEmpty);
        lock (handler.Requests)
            Assert.DoesNotContain(handler.Requests, uri => uri.Host == "evil.example");
    }
}
