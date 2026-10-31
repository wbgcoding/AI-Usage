using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

/// <summary>The plan forecast section of the statistics view model: which provider it shows and how the
/// buttons pick, the plan line, and the fractions behind the gauge, model and month bars.</summary>
public class StatsForecastViewModelTests
{
    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>Hourly tokens and a weekly quota that rises with them, one reading per hour.</summary>
    private static (List<StatsRecord> Records, List<HistoryPoint> Points) History(int hours, string provider)
    {
        var start = DateTime.Now;
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Local).AddHours(-hours - 1);
        var reset = DateTimeOffset.Now.AddDays(2);
        var random = new Random(9);
        var records = new List<StatsRecord>();
        var points = new List<HistoryPoint>();
        var percent = 0.0;
        for (var h = 0; h < hours; h++)
        {
            var moment = start.AddHours(h);
            points.Add(new HistoryPoint(1, new DateTimeOffset(moment), WindowKind.Weekly, percent, reset));
            var tokens = random.Next(100_000, 900_000);
            records.Add(new StatsRecord(provider, DateOnly.FromDateTime(moment), provider == "codex" ? "gpt-5.5" : "claude-opus-4-8", "p", tokens, 0, 0, 0, moment.Hour));
            percent += tokens / 2e6;
        }

        points.Add(new HistoryPoint(1, new DateTimeOffset(start.AddHours(hours)), WindowKind.Weekly, percent, reset));
        return (records, points);
    }

    private static async Task<(StatsViewModel ViewModel, IDisposable Directory)> CreateAsync(
        int hours, Func<string, IReadOnlyList<string>>? accountKeys = null, Func<string, string?>? plan = null,
        Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>>? history = null, bool withCodex = false,
        Func<List<StatsRecord>, IEnumerable<StatsRecord>>? extraRecords = null)
    {
        var directory = TestPaths.CreateDisposableDirectory("stats-forecast");
        var store = new StatsStore(directory);
        var (records, points) = History(hours, "claude");
        store.AddDelta(records);
        if (extraRecords is not null)
            store.AddDelta([.. extraRecords(records)]);
        var codexPoints = new List<HistoryPoint>();
        if (withCodex)
        {
            var (codexRecords, codexHistory) = History(hours, "codex");
            store.AddDelta(codexRecords);
            codexPoints = codexHistory;
        }

        var viewModel = new StatsViewModel(store)
        {
            LoadHistory = history ?? ((key, _, _) =>
                key.StartsWith("claude", StringComparison.Ordinal) ? points
                : key.StartsWith("codex", StringComparison.Ordinal) ? codexPoints : []),
        };
        if (accountKeys is not null)
            viewModel.AccountKeysOf = accountKeys;
        if (plan is not null)
            viewModel.PlanOf = plan;
        viewModel.Recompute();
        await viewModel.ForecastRefreshForTest;
        return (viewModel, directory);
    }

    private static CostService Prices(bool enabled = true, CostCurrency currency = CostCurrency.Usd)
    {
        var directory = TestPaths.CreateDisposableDirectory("stats-forecast-cost").Path;
        var service = new CostService(
            () => enabled, () => currency, directory, () => CultureInfo.GetCultureInfo("en-US"),
            () => new RegionInfo("US"), null, () => false);
        service.Prices.Replace(
            ModelPriceList.Parse("""{ "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "mode": "chat" } }"""),
            DateTimeOffset.UtcNow, persist: false);
        return service;
    }

    private static string Short(long tokens) =>
        StatsAggregator.ShortenTokenCount(tokens, Loc["Number.Thousand"], Loc["Number.Million"], Loc["Number.Billion"]);

    private static PerPercentResult Total(long perPercent) => new(PerPercentStatus.Total, 12, [], 5_000, perPercent);

    private static ForecastWindow Window(PerPercentResult result, double? apiPerPercent = 5.65, ForecastAverage? average = null) =>
        new(result, apiPerPercent, [], average);

    private static ProviderForecast Forecast(
        ForecastWindow? fiveHour, ForecastWindow? weekly = null, double? api = 4832, double? plan = 100, int accounts = 1, params string[] plans) =>
        new("claude", fiveHour, weekly, plans, accounts, api, plan,
            plan is { } usd ? [new PlanPrice(plans.Length > 0 ? plans[0] : "Max 5x", usd, usd * 0.9, "example.com", new DateOnly(2026, 10, 31))] : null);

    [Fact]
    public async Task A_provider_with_history_and_tokens_gets_a_block_with_its_plan_and_both_cards()
    {
        var (viewModel, directory) = await CreateAsync(100, plan: _ => "Max 5x");
        using (directory)
        {
            var provider = Assert.Single(viewModel.ForecastProviders);
            Assert.True(viewModel.HasForecast);
            Assert.Equal("claude", provider.ProviderId);
            Assert.Equal("Claude", provider.Name);
            Assert.Equal("Max 5x", provider.PlanText);
            Assert.Equal("Max 5x", provider.Detail);
            Assert.Equal("Max 5x", Assert.Single(viewModel.ForecastButtons).Detail);
            Assert.True(viewModel.HasSingleForecast);
            Assert.False(viewModel.HasForecastChoice);
            Assert.All(provider.Windows, window => Assert.False(string.IsNullOrEmpty(window.ToolTip)));
            Assert.Same(provider, Assert.Single(viewModel.ShownForecast));
        }
    }

    [Fact]
    public async Task Tokens_of_another_vendors_models_do_not_count_toward_the_forecast()
    {
        var (plain, plainDirectory) = await CreateAsync(100);
        var (mixed, mixedDirectory) = await CreateAsync(
            100, extraRecords: records => records.Select(record => record with { Model = "minimax-m3", InputTokens = record.InputTokens * 7 }));
        using (plainDirectory)
        using (mixedDirectory)
        {
            var expected = Assert.Single(plain.ForecastProviders).Windows.Select(window => window.CapacityText);
            var actual = Assert.Single(mixed.ForecastProviders).Windows.Select(window => window.CapacityText);
            Assert.Equal(expected, actual);
            Assert.All(Assert.Single(mixed.ForecastProviders).Windows.SelectMany(window => window.Models), bar => Assert.DoesNotContain("minimax", bar.Label));
        }
    }

    [Fact]
    public async Task A_provider_without_a_known_plan_has_no_plan_line()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
        {
            var provider = Assert.Single(viewModel.ForecastProviders);
            Assert.Equal("", provider.Detail);
            Assert.False(Assert.Single(viewModel.ForecastButtons).HasDetail);
        }
    }

    [Fact]
    public async Task Several_accounts_are_pooled_into_one_block_with_all_plans_and_the_count()
    {
        var plans = new Dictionary<string, string?> { ["claude"] = "Max 5x", ["claude#2"] = "Pro" };
        var (viewModel, directory) = await CreateAsync(
            100, id => id == "claude" ? ["claude", "claude#2"] : [id], key => plans.GetValueOrDefault(key));
        using (directory)
        {
            var provider = Assert.Single(viewModel.ForecastProviders);
            Assert.Equal("Max 5x + Pro", provider.PlanText);
            Assert.Equal(Loc.Format("Stats.Forecast.Accounts", 2), provider.AccountsText);
            Assert.Equal("Max 5x + Pro · " + provider.AccountsText, provider.Detail);
        }
    }

    [Fact]
    public async Task Too_few_hours_give_cards_that_only_say_not_enough_data()
    {
        var (viewModel, directory) = await CreateAsync(8);
        using (directory)
        {
            var provider = Assert.Single(viewModel.ForecastProviders);
            Assert.All(provider.Windows, window =>
            {
                Assert.False(window.HasData);
                Assert.Equal(Loc["Stats.Forecast.TooFew"], window.CapacityText);
                Assert.False(window.HasAverage);
                Assert.False(window.HasModels);
                Assert.False(window.IsAllModels);
            });
        }
    }

    [Fact]
    public async Task No_quota_history_hides_the_section()
    {
        var (viewModel, directory) = await CreateAsync(100, history: (_, _, _) => []);
        using (directory)
        {
            Assert.Empty(viewModel.ForecastProviders);
            Assert.Empty(viewModel.ShownForecast);
            Assert.False(viewModel.HasForecast);
            Assert.False(viewModel.HasForecastChoice);
        }
    }

    [Fact]
    public async Task One_provider_shows_no_button_row()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
        {
            Assert.Single(viewModel.ForecastButtons);
            Assert.False(viewModel.HasForecastChoice);
            Assert.Equal("claude", viewModel.SelectedForecastProvider);
        }
    }

    [Fact]
    public async Task Two_providers_show_the_buttons_and_the_first_one_by_default()
    {
        var (viewModel, directory) = await CreateAsync(100, withCodex: true);
        using (directory)
        {
            Assert.True(viewModel.HasForecastChoice);
            Assert.Equal(["claude", "codex"], viewModel.ForecastButtons.Select(button => button.ProviderId));
            Assert.Equal("claude", viewModel.SelectedForecastProvider);
            Assert.Equal(["claude"], viewModel.ShownForecast.Select(provider => provider.ProviderId));
            Assert.Equal([true, false], viewModel.ForecastButtons.Select(button => button.IsChecked));
        }
    }

    [Fact]
    public async Task A_picked_provider_is_shown_and_kept_while_the_window_stays_open()
    {
        var (viewModel, directory) = await CreateAsync(100, withCodex: true);
        using (directory)
        {
            viewModel.ForecastButtons[1].IsChecked = true;
            Assert.Equal("codex", viewModel.SelectedForecastProvider);
            Assert.Equal(["codex"], viewModel.ShownForecast.Select(provider => provider.ProviderId));
            Assert.Equal([false, true], viewModel.ForecastButtons.Select(button => button.IsChecked));

            // A redraw (range change) keeps the pick.
            await viewModel.SetRangeCommand.ExecuteAsync("ThisMonth");
            await viewModel.ForecastRefreshForTest;
            Assert.Equal("codex", viewModel.SelectedForecastProvider);
        }
    }

    [Fact]
    public async Task The_single_provider_of_the_filter_is_shown_when_nothing_was_picked()
    {
        var (viewModel, directory) = await CreateAsync(100, withCodex: true);
        using (directory)
        {
            viewModel.SetProviderCommand.Execute("codex");
            await viewModel.ForecastRefreshForTest;
            Assert.Equal("codex", viewModel.SelectedForecastProvider);
            Assert.Single(viewModel.ForecastButtons);
            Assert.False(viewModel.HasForecastChoice);

            viewModel.SetProviderCommand.Execute("");
            await viewModel.ForecastRefreshForTest;
            Assert.Equal("claude", viewModel.SelectedForecastProvider);
            Assert.True(viewModel.HasForecastChoice);
        }
    }

    [Fact]
    public async Task A_pick_beats_the_filter()
    {
        var (viewModel, directory) = await CreateAsync(100, withCodex: true);
        using (directory)
        {
            viewModel.ForecastButtons[0].IsChecked = true;
            viewModel.SetProviderCommand.Execute("claude,codex");
            await viewModel.ForecastRefreshForTest;
            viewModel.ForecastButtons[1].IsChecked = true;
            Assert.Equal("codex", viewModel.SelectedForecastProvider);
        }
    }

    [Fact]
    public async Task The_provider_filter_applies_and_hides_the_section_when_it_excludes_every_forecast()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
        {
            viewModel.SetProviderCommand.Execute("gemini");
            await viewModel.ForecastRefreshForTest;
            Assert.False(viewModel.HasForecast);

            viewModel.SetProviderCommand.Execute("claude");
            await viewModel.ForecastRefreshForTest;
            Assert.True(viewModel.HasForecast);

            viewModel.SetProviderCommand.Execute("");
            await viewModel.ForecastRefreshForTest;
            Assert.True(viewModel.HasForecast);
        }
    }

    [Fact]
    public async Task The_time_range_does_not_change_the_forecast()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
        {
            var before = Assert.Single(viewModel.ForecastProviders).Windows.Select(window => window.CapacityText).ToList();
            await viewModel.SetRangeCommand.ExecuteAsync("ThisMonth");
            await viewModel.ForecastRefreshForTest;
            Assert.Equal(before, Assert.Single(viewModel.ForecastProviders).Windows.Select(window => window.CapacityText));
        }
    }

    [Fact]
    public async Task An_account_key_seam_that_throws_falls_back_to_the_providers_own_history()
    {
        var (viewModel, directory) = await CreateAsync(100, id => throw new InvalidOperationException("collection modified"));
        using (directory)
            Assert.Single(viewModel.ForecastProviders);
    }

    [Fact]
    public void The_average_fills_the_gauge_to_the_average_peak_and_is_labelled()
    {
        var forecast = Forecast(null, Window(Total(172_000), average: new ForecastAverage(9, 2_150_000, 10.0)), plan: null);
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.True(window.HasData);
        Assert.Equal(0.10, window.AverageFraction, 6);
        Assert.True(window.HasAverage);
        Assert.Contains(Short(2_150_000), window.AverageText);
        Assert.Contains("10", window.AverageText);
        Assert.Equal(Loc.Format("Stats.Forecast.Capacity", Short(17_200_000)), window.CapacityText);
    }

    [Fact]
    public void An_average_above_the_capacity_shows_its_own_share_of_it_instead_of_the_smaller_peak()
    {
        var forecast = Forecast(null, Window(Total(172_000), average: new ForecastAverage(4, 18_060_000, 98.0)), plan: null);
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.Equal(1.0, window.AverageFraction, 6);
        Assert.Contains("105", window.AverageText);
        Assert.DoesNotContain("98", window.AverageText);
    }

    [Fact]
    public void No_average_gives_an_empty_gauge_and_no_label()
    {
        var forecast = Forecast(null, Window(Total(172_000)));
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.Equal(0, window.AverageFraction);
        Assert.False(window.HasAverage);
    }

    [Fact]
    public void An_average_above_the_window_is_capped_at_a_full_gauge()
    {
        var forecast = Forecast(null, Window(Total(172_000), average: new ForecastAverage(3, 1_000, 140.0)));
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);
        Assert.Equal(1.0, window.AverageFraction);
    }

    [Fact]
    public void Model_bars_are_relative_to_the_longest_model()
    {
        var items = new List<PerPercentItem>
        {
            new("Opus 4.8", false, 1_050_000),
            new("Sonnet 4.5", false, 2_100_000),
            new("", true, 525_000),
        };
        var perModel = new PerPercentResult(PerPercentStatus.PerModel, 14, items, 0, 800_000);
        var forecast = Forecast(null, Window(perModel));
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.True(window.HasModels);
        Assert.False(window.IsAllModels);
        Assert.Equal(Loc["Stats.Forecast.ModelsCaption"], window.ModelsCaption);
        Assert.Equal([0.5, 1.0, 0.25], window.Models.Select(bar => bar.Fraction));
        Assert.Equal("Opus 4.8 · " + Short(105_000_000), window.Models[0].Label);
        Assert.Contains(Loc["Stats.Other"], window.Models[2].Label);
    }

    [Fact]
    public void A_single_total_has_no_model_bars_and_says_all_models_together()
    {
        var forecast = Forecast(null, Window(Total(172_000)));
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.False(window.HasModels);
        Assert.True(window.IsAllModels);
        Assert.Equal(Loc["Stats.Forecast.AllModels"], window.ModelsCaption);
    }

    [Fact]
    public void A_window_with_too_few_intervals_shows_only_the_message()
    {
        var forecast = Forecast(null, Window(PerPercentResult.TooFew(3), average: new ForecastAverage(2, 1_000, 5.0)));
        var window = Assert.Single(StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Windows);

        Assert.False(window.HasData);
        Assert.Equal(Loc["Stats.Forecast.TooFew"], window.CapacityText);
        Assert.False(window.HasApi);
        Assert.False(window.HasAverage);
        Assert.Equal(0, window.AverageFraction);
        Assert.Empty(window.Models);
    }

    [Fact]
    public void The_month_card_shows_the_ratio_and_the_plan_with_its_price_and_source()
    {
        var forecast = Forecast(Window(Total(172_000)), Window(Total(1_050_000)), api: 4832, plan: 100, plans: "Max 5x");
        var provider = StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc);

        Assert.All(provider.Windows, window => Assert.True(window.HasApi));
        var month = Assert.IsType<StatsForecastMonthViewModel>(provider.Month);
        Assert.True(month.HasPlan);
        Assert.True(month.HasRatio);
        Assert.StartsWith("Max 5x ", month.PlanText);
        Assert.Contains("100", month.PlanText);
        Assert.Contains("example.com", month.PlanToolTip);
        Assert.Contains(new DateOnly(2026, 10, 31).ToString("d", CultureInfo.CurrentCulture), month.PlanToolTip);
        Assert.Equal((4832 / 100.0).ToString("0.0", CultureInfo.CurrentCulture) + "×", month.RatioText);
        Assert.Equal(Loc["Stats.Forecast.RatioCaption"], month.RatioCaption);
        Assert.Contains("4,832", month.ApiText);
    }

    [Fact]
    public void In_euros_the_card_shows_the_euro_list_price_and_compares_against_it()
    {
        var forecast = Forecast(Window(Total(172_000)), api: 4832, plan: 100, plans: "Max 5x");
        var service = Prices(currency: CostCurrency.Eur);
        service.Rate.Set(1.2, DateTimeOffset.UtcNow, persist: false);
        var month = Assert.IsType<StatsForecastMonthViewModel>(
            StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(service), Loc).Month);

        Assert.Contains("€", month.PlanText);
        Assert.Contains("90", month.PlanText);
        // 4832 dollars are 4026.67 euros, against the 90 euro list price.
        Assert.Equal((4832 / 1.2 / 90).ToString("0.0", CultureInfo.CurrentCulture) + "×", month.RatioText);
    }

    [Fact]
    public void A_plan_dearer_than_its_api_value_gives_a_ratio_below_one()
    {
        var forecast = Forecast(Window(Total(172_000)), api: 50, plan: 200);
        var month = Assert.IsType<StatsForecastMonthViewModel>(
            StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Month);

        Assert.Equal(0.25.ToString("0.0", CultureInfo.CurrentCulture) + "×", month.RatioText);
    }

    [Fact]
    public void Without_a_plan_price_there_is_no_plan_line_and_no_ratio()
    {
        var forecast = Forecast(Window(Total(172_000)), api: 4832, plan: null);
        var month = Assert.IsType<StatsForecastMonthViewModel>(
            StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices()), Loc).Month);

        Assert.False(month.HasPlan);
        Assert.False(month.HasRatio);
        Assert.False(month.HasSide);
    }

    [Fact]
    public void With_cost_off_there_is_no_money_and_no_month_card()
    {
        var forecast = Forecast(Window(Total(172_000)), Window(Total(1_050_000)));
        var provider = StatsViewModel.BuildForecastProvider(forecast, new StatsCostView(Prices(enabled: false)), Loc);

        Assert.Null(provider.Month);
        Assert.False(provider.HasMonth);
        Assert.All(provider.Windows, window =>
        {
            Assert.True(window.HasData);
            Assert.False(window.HasApi);
        });
    }
}
