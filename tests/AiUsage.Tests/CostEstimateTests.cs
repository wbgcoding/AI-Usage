using System.Globalization;
using System.Net;
using System.Net.Http;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

public class CostEstimateTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    private const string PriceJson = """
    {
      "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "cache_read_input_token_cost": 0.0000003, "cache_creation_input_token_cost": 0.00000375, "mode": "chat" },
      "gpt-5": { "input_cost_per_token": 0.00000125, "output_cost_per_token": 0.00001, "cache_read_input_token_cost": 0.000000125, "mode": "responses" }
    }
    """;

    private const string EcbXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <gesmes:Envelope xmlns:gesmes="http://www.gribuniter.org/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
      <Cube><Cube time="2026-10-16"><Cube currency="USD" rate="1.25"/><Cube currency="JPY" rate="180.1"/></Cube></Cube>
    </gesmes:Envelope>
    """;

    private static string TempDirectory() => TestPaths.GetPath("ai-usage-cost");

    private static ModelPriceList Prices()
    {
        var list = new ModelPriceList(TempDirectory);
        list.Replace(ModelPriceList.Parse(PriceJson), DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    private static StatsRecord Row(string model, long input, long output, long creation = 0, long read = 0) =>
        new("claude", Day, model, "p", input, output, creation, read);

    // ---- math ----

    [Fact]
    public void Cost_prices_the_four_counts_separately()
    {
        var price = new ModelPrice(0.000003, 0.000015, 0.0000003, 0.00000375);
        var cost = CostCalculator.Cost(price, Row("claude-sonnet-4-5", 1_000_000, 100_000, 200_000, 500_000));
        Assert.Equal(3.0 + 1.5 + 0.75 + 0.15, cost, 6);
    }

    [Fact]
    public void A_missing_cache_price_falls_back_to_the_input_price()
    {
        var price = new ModelPrice(0.000002, 0.00001, null, null);
        Assert.Equal(2.0 + 2.0 + 1.0, CostCalculator.Cost(price, Row("m", 1_000_000, 0, 1_000_000, 500_000)), 6);
    }

    [Fact]
    public void Sum_adds_priced_models_and_names_the_unpriced_ones()
    {
        var summary = CostCalculator.Sum(
            [Row("claude-sonnet-4-5", 1_000_000, 0), Row("claude-sonnet-4-5-20251001", 1_000_000, 0), Row("mystery-model", 500, 500)],
            Prices());
        Assert.True(summary.AnyPriced);
        Assert.Equal(6.0, summary.Usd, 6);
        Assert.Equal(["mystery-model"], summary.UnpricedModels);
    }

    [Fact]
    public void Sum_of_only_unpriced_models_is_not_priced()
    {
        var summary = CostCalculator.Sum([Row("mystery-model", 10, 10)], Prices());
        Assert.False(summary.AnyPriced);
        Assert.True(summary.HasUnpriced);
    }

    [Fact]
    public void Sum_ignores_rows_without_tokens()
    {
        var summary = CostCalculator.Sum([Row("mystery-model", 0, 0)], Prices());
        Assert.False(summary.AnyPriced);
        Assert.False(summary.HasUnpriced);
    }

    [Fact]
    public void Codex_row_prices_the_stored_input_without_subtracting_the_cache_again()
    {
        // The parsers store input as the uncached share, so a Codex row of 100 uncached, 400 cache read
        // costs 100 * input + 400 * cache read, never 0 * input.
        var price = Prices().Find("gpt-5")!;
        var row = new StatsRecord("codex", Day, "gpt-5", "p", 100, 10, 0, 400);
        Assert.Equal(100 * 0.00000125 + 10 * 0.00001 + 400 * 0.000000125, CostCalculator.Cost(price, row), 9);
    }

    [Fact]
    public void The_Claude_parser_stores_input_apart_from_both_cache_counts()
    {
        const string line = """{"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"claude-opus-5","usage":{"input_tokens":100,"cache_creation_input_tokens":300,"cache_read_input_tokens":400,"output_tokens":10}}}""";
        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(300, result.CacheCreationTokens);
        Assert.Equal(400, result.CacheReadTokens);
    }

    [Fact]
    public void The_Antigravity_parser_stores_input_apart_from_the_cache_read()
    {
        Assert.True(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation("gemini-3-pro", 100, 400, 4, 6, step: null), out var generation));
        Assert.Equal(100, generation.InputTokens);
        Assert.Equal(400, generation.CacheReadTokens);
        Assert.Equal(10, generation.OutputTokens);
    }

    // ---- formatting ----

    [Fact]
    public void German_format_has_the_tilde_then_the_currency_format()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("~2,00\u00A0€", CostFormatter.Format(2.5, CostCurrency.Eur, 1.25, de));
        Assert.Equal("~12,34\u00A0$", CostFormatter.Format(12.34, CostCurrency.Usd, null, de));
    }

    [Fact]
    public void English_format_puts_the_symbol_in_front()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("~$2.00", CostFormatter.Format(2.0, CostCurrency.Usd, null, en));
        Assert.Equal("~$1,234.50", CostFormatter.Format(1234.5, CostCurrency.Usd, null, en));
    }

    [Fact]
    public void Below_a_cent_shows_a_cent_and_no_less_than_sign()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("~0,01\u00A0$", CostFormatter.Format(0.0004, CostCurrency.Usd, null, de));
        Assert.Equal("~$0.01", CostFormatter.Format(0.0004, CostCurrency.Usd, null, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Euro_without_a_rate_shows_dollars()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("~2,00\u00A0$", CostFormatter.Format(2.0, CostCurrency.Eur, null, de));
    }

    [Fact]
    public void A_summary_with_unpriced_models_gets_a_star_and_nothing_priced_shows_a_question_mark()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("~$2.00*", CostFormatter.Format(new CostSummary(2.0, true, ["x"]), CostCurrency.Usd, null, en));
        Assert.Equal("~$2.00", CostFormatter.Format(new CostSummary(2.0, true, []), CostCurrency.Usd, null, en));
        Assert.Equal("?*", CostFormatter.Format(new CostSummary(0, false, ["x"]), CostCurrency.Usd, null, en));
        Assert.Equal("", CostFormatter.Format(CostSummary.Empty, CostCurrency.Usd, null, en));
    }

    [Fact]
    public void Export_value_is_a_plain_invariant_dollar_number()
    {
        Assert.Equal("1.5", CostService.UsdForExport(new CostSummary(1.5, true, [])));
        Assert.Equal("", CostService.UsdForExport(new CostSummary(0, false, ["x"])));
    }

    // ---- currency ----

    [Fact]
    public void Auto_follows_the_region_currency()
    {
        Assert.Equal(CostCurrency.Eur, CostFormatter.Effective(CostCurrency.Auto, new RegionInfo("de-DE")));
        Assert.Equal(CostCurrency.Usd, CostFormatter.Effective(CostCurrency.Auto, new RegionInfo("en-US")));
        Assert.Equal(CostCurrency.Eur, CostFormatter.Effective(CostCurrency.Eur, new RegionInfo("en-US")));
        Assert.Equal(CostCurrency.Usd, CostFormatter.Effective(CostCurrency.Usd, new RegionInfo("de-DE")));
    }

    [Fact]
    public void ParseEcb_reads_the_dollar_rate_and_rejects_everything_else()
    {
        Assert.Equal(1.25, EurRate.ParseEcb(EcbXml));
        Assert.Null(EurRate.ParseEcb("not xml"));
        Assert.Null(EurRate.ParseEcb(EcbXml.Replace("rate=\"1.25\"", "rate=\"0\"")));
        Assert.Null(EurRate.ParseEcb(EcbXml.Replace("rate=\"1.25\"", "rate=\"9999\"")));
        Assert.Null(EurRate.ParseEcb(EcbXml.Replace("rate=\"1.25\"", "rate=\"abc\"")));
        Assert.Null(EurRate.ParseEcb("<!DOCTYPE a [<!ENTITY e SYSTEM \"file:///c:/x\">]><a>&e;</a>"));
    }

    [Fact]
    public void The_rate_cache_round_trips_and_goes_stale_after_a_day()
    {
        var directory = TempDirectory();
        var written = new EurRate(() => directory);
        var at = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
        written.Set(1.25, at);

        var read = new EurRate(() => directory);
        read.Load();
        Assert.Equal(1.25, read.UsdPerEur);
        Assert.False(read.IsStale(at.AddHours(23)));
        Assert.True(read.IsStale(at.AddHours(25)));
    }

    // ---- service ----

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage ForUrl(Uri uri) => uri.Host switch
    {
        "raw.githubusercontent.com" => Ok(PriceJson),
        "www.ecb.europa.eu" => Ok(EcbXml),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static CostService Service(
        FakeHandler handler, string directory, CostCurrency currency = CostCurrency.Usd, bool enabled = true,
        bool networkAllowed = true, Func<DateTimeOffset>? now = null) =>
        new(() => enabled, () => currency, directory, () => CultureInfo.GetCultureInfo("en-US"), () => new RegionInfo("US"),
            now ?? (() => DateTimeOffset.UtcNow), () => networkAllowed, handler);

    [Fact]
    public async Task Refresh_loads_the_price_list_from_the_one_host_and_caches_it()
    {
        var directory = TempDirectory();
        var handler = new FakeHandler(ForUrl);
        var service = Service(handler, directory);
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshAsync();

        Assert.Equal([new Uri(PriceDownloader.PriceListUrl)], handler.Requests);
        Assert.False(service.Prices.IsEmpty);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(Path.Combine(directory, ModelPriceList.CacheFileName)));
        Assert.Empty(Directory.GetFiles(directory, "download-*"));

        var again = Service(new FakeHandler(ForUrl), directory);
        Assert.False(again.Prices.IsEmpty);
    }

    [Fact]
    public async Task Refresh_does_nothing_when_off_or_when_the_network_is_held()
    {
        var off = new FakeHandler(ForUrl);
        await Service(off, TempDirectory(), enabled: false).RefreshAsync();
        Assert.Empty(off.Requests);

        var held = new FakeHandler(ForUrl);
        await Service(held, TempDirectory(), networkAllowed: false).RefreshAsync();
        Assert.Empty(held.Requests);
    }

    [Fact]
    public async Task A_fresh_cache_is_not_downloaded_again()
    {
        var directory = TempDirectory();
        await Service(new FakeHandler(ForUrl), directory).RefreshAsync();

        var second = new FakeHandler(ForUrl);
        await Service(second, directory).RefreshAsync();
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_cache_and_waits_an_hour()
    {
        var directory = TempDirectory();
        var clock = DateTimeOffset.UtcNow;
        await Service(new FakeHandler(ForUrl), directory, now: () => clock).RefreshAsync();

        var failing = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var later = clock.AddDays(2);
        var service = Service(failing, directory, now: () => later);
        await service.RefreshAsync();
        Assert.Single(failing.Requests);
        Assert.False(service.Prices.IsEmpty);

        await service.RefreshAsync();
        Assert.Single(failing.Requests);

        later = later.AddMinutes(61);
        await service.RefreshAsync();
        Assert.Equal(2, failing.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused()
    {
        var handler = new FakeHandler(uri => uri.Host == "raw.githubusercontent.com"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/prices.json") } }
            : Ok(PriceJson));
        var service = Service(handler, TempDirectory());
        await service.RefreshAsync();
        Assert.True(service.Prices.IsEmpty);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_body_without_usable_prices_does_not_replace_the_list()
    {
        var service = Service(new FakeHandler(_ => Ok("[1,2,3]")), TempDirectory());
        await service.RefreshAsync();
        Assert.True(service.Prices.IsEmpty);
    }

    [Fact]
    public async Task The_euro_rate_is_fetched_only_when_euro_is_shown()
    {
        var usd = new FakeHandler(ForUrl);
        await Service(usd, TempDirectory(), CostCurrency.Usd).RefreshAsync();
        Assert.DoesNotContain(usd.Requests, uri => uri.Host == "www.ecb.europa.eu");

        var eur = new FakeHandler(ForUrl);
        var service = Service(eur, TempDirectory(), CostCurrency.Eur);
        await service.RefreshAsync();
        Assert.Contains(eur.Requests, uri => uri.Host == "www.ecb.europa.eu");
        Assert.Equal(1.25, service.Rate.UsdPerEur);
    }

    [Fact]
    public async Task Text_is_empty_when_off_and_shows_dollars_until_a_rate_is_known()
    {
        var directory = TempDirectory();
        var service = Service(new FakeHandler(ForUrl), directory, CostCurrency.Eur, networkAllowed: false);
        service.Prices.Replace(ModelPriceList.Parse(PriceJson), DateTimeOffset.UtcNow, persist: false);
        var records = new[] { Row("claude-sonnet-4-5", 1_000_000, 0) };
        Assert.Equal("~$3.00", service.Text(records));

        var off = Service(new FakeHandler(ForUrl), directory, enabled: false);
        Assert.Equal("", off.Text(records));
        Assert.Equal(CostSummary.Empty, off.Sum(records));
        await Task.CompletedTask;
    }

    [Fact]
    public void Without_prices_nothing_is_estimated()
    {
        var service = Service(new FakeHandler(ForUrl), TempDirectory());
        Assert.Equal("", service.Text([Row("claude-sonnet-4-5", 1_000_000, 0)]));
    }

    // ---- settings ----

    [Fact]
    public void Settings_default_to_on_and_automatic_currency_and_read_old_files()
    {
        var settings = new AppSettings();
        Assert.True(settings.ShowCostEstimate);
        Assert.Equal(CostCurrency.Auto, settings.CostCurrency);
        Assert.Equal(CostCurrency.Auto, System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.CostCurrency);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "CostCurrency": "bogus" }""")!;
        Assert.Equal(CostCurrency.Usd, parsed.CostCurrency);
        Assert.True(parsed.ShowCostEstimate);
        Assert.Equal(CostCurrency.Auto, System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "CostCurrency": "auto" }""")!.CostCurrency);
    }
}
