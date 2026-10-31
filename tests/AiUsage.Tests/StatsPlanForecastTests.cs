using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

public class PlanPriceListTests
{
    [Theory]
    [InlineData("claude", "Pro", 20)]
    [InlineData("claude", "Max 5x", 100)]
    [InlineData("claude", "Max 20x", 200)]
    [InlineData("codex", "Plus", 20)]
    [InlineData("codex", "Pro", 200)]
    [InlineData("codex", "Business", 30)]
    [InlineData("codex", "Team", 30)]
    [InlineData("gemini", "Pro", 19.99)]
    public void A_known_plan_has_its_list_price(string provider, string plan, double expected) =>
        Assert.Equal(expected, PlanPriceList.MonthlyUsd(provider, plan));

    [Theory]
    [InlineData("claude", "Team")]
    [InlineData("claude", "Free")]
    [InlineData("claude", null)]
    [InlineData("claude", "")]
    [InlineData("cursor", "Pro")]
    [InlineData("gemini", "Plus")]
    [InlineData("gemini", "Ultra")]
    public void An_unknown_plan_has_no_price(string provider, string? plan) =>
        Assert.Null(PlanPriceList.MonthlyUsd(provider, plan));

    [Fact]
    public void The_lookup_ignores_case_and_the_account_suffix_of_the_provider()
    {
        Assert.Equal(100, PlanPriceList.MonthlyUsd("claude#2", "max 5x"));
        Assert.Equal(20, PlanPriceList.MonthlyUsd("CODEX", "PLUS"));
    }

    [Fact]
    public void Every_price_names_its_plan_its_source_and_when_it_was_checked()
    {
        var max = PlanPriceList.Find("claude", "max 5x")!;
        Assert.Equal("Max 5x", max.Plan);
        Assert.Equal(107.10, max.Eur);
        Assert.False(string.IsNullOrWhiteSpace(max.Source));
        Assert.True(max.CheckedOn.Year >= 2026);
        Assert.Null(PlanPriceList.Find("codex", "business")!.Eur);
    }
}

public class StatsPlanForecastTests
{
    private const string Sonnet = "claude-sonnet-4-5";

    private static readonly DateTime Start = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Local);

    private static StatsRecord Record(int hour, long tokens, string model = Sonnet, long cacheRead = 0)
    {
        var at = Start.AddHours(hour);
        return new StatsRecord("claude", DateOnly.FromDateTime(at), model, "", tokens, 0, 0, cacheRead, at.Hour);
    }

    private static List<StatsRecord> Records(int hours, long tokens = 1000) =>
        Enumerable.Range(0, hours).Select(hour => Record(hour, tokens)).ToList();

    private static List<HistoryPoint> Points(
        int hours, double percentPerHour, WindowKind kind = WindowKind.Weekly, int fromHour = 0, TimeSpan? resetIn = null)
    {
        var reset = new DateTimeOffset(Start) + (resetIn ?? TimeSpan.FromDays(3));
        return Enumerable.Range(fromHour, hours + 1 - fromHour)
            .Select(hour => new HistoryPoint(1, new DateTimeOffset(Start).AddHours(hour), kind, hour * percentPerHour, reset))
            .ToList();
    }

    private static readonly ModelPriceList Prices = CreatePrices();

    private static ModelPriceList CreatePrices()
    {
        var list = new ModelPriceList(() => TestPaths.CreateDisposableDirectory("forecast-prices").Path);
        list.Replace(ModelPriceList.Parse(
            """{ "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "mode": "chat" } }"""),
            DateTimeOffset.UtcNow, persist: false);
        return list;
    }

    private static CostSummary Price(IReadOnlyList<StatsRecord> rows) => CostCalculator.Sum(rows, Prices);

    [Fact]
    public void Two_accounts_pool_into_one_interval_list_with_the_summed_delta()
    {
        var intervals = StatsTokensPerPercent.BuildPooledIntervals(
            Records(30), [Points(30, 0.5), Points(30, 0.25)], TimeZoneInfo.Local, null);

        Assert.Equal(30, intervals.Count);
        Assert.All(intervals, interval =>
        {
            Assert.Equal(0.75, interval.DeltaPercent, 6);
            Assert.Equal(1000, interval.TokensByModel["Sonnet 4.5"]);
        });
    }

    [Fact]
    public void An_hour_one_pooled_account_has_no_reading_for_is_left_out()
    {
        var gap = Points(30, 0.25).Where(point => point.Timestamp != new DateTimeOffset(Start).AddHours(10)).ToList();

        var intervals = StatsTokensPerPercent.BuildPooledIntervals(
            Records(30), [Points(30, 0.5), gap], TimeZoneInfo.Local, null);

        // Hours 9 and 10 each lose one end of the second account.
        Assert.Equal(28, intervals.Count);
    }

    [Fact]
    public void An_account_counts_only_for_the_hours_between_its_own_first_and_last_reading()
    {
        var intervals = StatsTokensPerPercent.BuildPooledIntervals(
            Records(30), [Points(30, 0.5), Points(30, 0.25, fromHour: 15)], TimeZoneInfo.Local, null);

        Assert.Equal(30, intervals.Count);
        Assert.Equal(15, intervals.Count(interval => Math.Abs(interval.DeltaPercent - 0.5) < 1e-9));
        Assert.Equal(15, intervals.Count(interval => Math.Abs(interval.DeltaPercent - 0.75) < 1e-9));
    }

    [Fact]
    public void One_account_gives_the_same_intervals_as_the_single_account_builder()
    {
        var records = Records(30);
        var points = Points(30, 0.5);

        var single = StatsTokensPerPercent.BuildIntervals(records, points);
        var pooled = StatsTokensPerPercent.BuildPooledIntervals(records, [points], TimeZoneInfo.Local, null);

        Assert.Equal(single.Count, pooled.Count);
        Assert.Equal(single.Select(i => i.DeltaPercent), pooled.Select(i => i.DeltaPercent));
    }

    [Fact]
    public void An_interval_carries_the_api_cost_of_its_hours_and_names_unpriced_models()
    {
        var records = Records(25);
        records.Add(Record(3, 500, "mystery-model"));

        var intervals = StatsTokensPerPercent.BuildPooledIntervals(records, [Points(25, 0.5)], TimeZoneInfo.Local, Price);

        Assert.All(intervals, interval => Assert.True(interval.AnyPriced));
        Assert.Equal(0.003, intervals[0].CostUsd, 9);
        Assert.Contains("mystery-model", intervals[3].UnpricedModels!);
        Assert.Null(intervals[0].UnpricedModels);
    }

    [Fact]
    public void A_provider_without_any_window_history_has_no_forecast()
    {
        Assert.Null(StatsPlanForecast.Build("claude", [new ForecastAccount("claude", "Pro", [])], Records(30), null, DateTimeOffset.Now, TimeZoneInfo.Local));
        Assert.Null(StatsPlanForecast.Build("claude", [], Records(30), null, DateTimeOffset.Now, TimeZoneInfo.Local));
    }

    [Fact]
    public void The_five_hour_and_the_weekly_window_are_estimated_apart_with_their_own_history()
    {
        // The weekly window rises 0.5 % an hour, the five-hour window 2 % an hour.
        var account = new ForecastAccount("claude", "Max 5x", [.. Points(30, 0.5), .. Points(30, 2, WindowKind.FiveHour)]);

        var forecast = StatsPlanForecast.Build("claude", [account], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        Assert.Equal(PerPercentStatus.Total, forecast.Weekly!.Result.Status);
        Assert.Equal(PerPercentStatus.Total, forecast.FiveHour!.Result.Status);
        // 30 000 tokens over 15 % and over 60 %.
        Assert.Equal(2000, forecast.Weekly.Result.TotalPerPercent);
        Assert.Equal(500, forecast.FiveHour.Result.TotalPerPercent);
        Assert.Equal(200_000, forecast.Weekly.CapacityTokens);
        Assert.Equal(50_000, forecast.FiveHour.CapacityTokens);
        Assert.Equal(["Max 5x"], forecast.Plans);
        Assert.Equal(1, forecast.AccountCount);
    }

    [Fact]
    public void Too_few_hours_give_a_too_few_window_without_figures()
    {
        var account = new ForecastAccount("claude", null, Points(10, 0.5));

        var forecast = StatsPlanForecast.Build("claude", [account], Records(10), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        Assert.Equal(PerPercentStatus.TooFew, forecast.Weekly!.Result.Status);
        Assert.Null(forecast.Weekly.ApiUsdPerPercent);
        Assert.Null(forecast.FiveHour);
    }

    [Fact]
    public void The_api_value_is_the_cost_of_the_intervals_per_percent_times_a_hundred()
    {
        var account = new ForecastAccount("claude", "Pro", Points(30, 0.5));

        var window = StatsPlanForecast.Build("claude", [account], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!.Weekly!;

        // 30 hours of 1000 input tokens at $3 per million = $0.09 over 15 %.
        Assert.Equal(0.006, window.ApiUsdPerPercent!.Value, 9);
        Assert.Equal(0.6, window.ApiUsdPerWindow!.Value, 9);
        Assert.Empty(window.UnpricedModels);
    }

    [Fact]
    public void Without_a_price_function_there_is_no_api_value()
    {
        var account = new ForecastAccount("claude", "Pro", Points(30, 0.5));

        var window = StatsPlanForecast.Build("claude", [account], Records(30), null, DateTimeOffset.Now, TimeZoneInfo.Local)!.Weekly!;

        Assert.Null(window.ApiUsdPerPercent);
        Assert.Null(window.ApiUsdPerWindow);
    }

    [Fact]
    public void Models_without_a_price_are_named_and_cost_nothing_in_the_ratio()
    {
        var records = Records(30);
        records.AddRange(Enumerable.Range(0, 30).Select(hour => Record(hour, 1000, "mystery-model")));
        var account = new ForecastAccount("claude", "Pro", Points(30, 0.5));

        var window = StatsPlanForecast.Build("claude", [account], records, Price, DateTimeOffset.Now, TimeZoneInfo.Local)!.Weekly!;

        Assert.Equal(["mystery-model"], window.UnpricedModels);
        Assert.Equal(0.006, window.ApiUsdPerPercent!.Value, 9);
    }

    [Fact]
    public void The_month_value_is_the_weekly_value_over_a_month_and_the_ratio_divides_by_the_plan_price()
    {
        var account = new ForecastAccount("claude", "Pro", Points(30, 0.5));

        var forecast = StatsPlanForecast.Build("claude", [account], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        // $0.6 per full week x 30.44 / 7 days.
        Assert.Equal(0.6 * 30.44 / 7, forecast.MonthlyApiUsd!.Value, 9);
        Assert.Equal(20, forecast.MonthlyPlanUsd);
        Assert.Equal(0.6 * 30.44 / 7 / 20, forecast.Ratio!.Value, 9);
    }

    [Fact]
    public void Several_accounts_multiply_the_month_value_and_add_up_their_plan_prices()
    {
        var one = new ForecastAccount("claude", "Max 5x", Points(30, 0.25));
        var two = new ForecastAccount("claude#2", "Pro", Points(30, 0.25));

        var forecast = StatsPlanForecast.Build("claude", [one, two], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        Assert.Equal(2, forecast.AccountCount);
        Assert.Equal(["Max 5x", "Pro"], forecast.Plans);
        Assert.Equal(120, forecast.MonthlyPlanUsd);
        // 30 000 tokens over 15 % of summed delta = 2000 per percent of one account.
        Assert.Equal(2000, forecast.Weekly!.Result.TotalPerPercent);
        Assert.Equal(2 * 0.6 * 30.44 / 7, forecast.MonthlyApiUsd!.Value, 9);
    }

    [Fact]
    public void The_same_plan_on_two_accounts_is_named_once()
    {
        var one = new ForecastAccount("claude", "Pro", Points(30, 0.25));
        var two = new ForecastAccount("claude#2", "Pro", Points(30, 0.25));

        var forecast = StatsPlanForecast.Build("claude", [one, two], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        Assert.Equal(["Pro"], forecast.Plans);
        Assert.Equal(40, forecast.MonthlyPlanUsd);
    }

    [Fact]
    public void One_unknown_plan_means_no_price_and_no_ratio()
    {
        var one = new ForecastAccount("claude", "Pro", Points(30, 0.25));
        var two = new ForecastAccount("claude#2", null, Points(30, 0.25));

        var forecast = StatsPlanForecast.Build("claude", [one, two], Records(30), Price, DateTimeOffset.Now, TimeZoneInfo.Local)!;

        Assert.Null(forecast.MonthlyPlanUsd);
        Assert.Null(forecast.Ratio);
        Assert.NotNull(forecast.MonthlyApiUsd);
    }

    [Fact]
    public void The_average_is_taken_over_finished_periods_that_saw_use()
    {
        // Three weekly periods of one day each, ending 1, 2 and 3 days after the start of the data; tokens
        // 12 000, 24 000 (peak 40 %) and none in the third, which never moved. The last has not ended yet.
        var now = new DateTimeOffset(Start).AddDays(3);
        List<HistoryPoint> points = [];
        var tokens = new List<StatsRecord>();
        // Weekly periods are 7 days long, so reset 7 days after each period start.
        for (var period = 0; period < 3; period++)
        {
            var begin = new DateTimeOffset(Start).AddDays(-21 + period * 7);
            var reset = begin.AddDays(7);
            var peak = period switch { 0 => 20, 1 => 40, _ => 0 };
            points.Add(new HistoryPoint(1, begin.AddHours(5), WindowKind.Weekly, 0, reset));
            points.Add(new HistoryPoint(1, begin.AddHours(6), WindowKind.Weekly, peak, reset));
            var each = period switch { 0 => 12_000, 1 => 24_000, _ => 0 };
            if (each > 0)
                tokens.Add(RecordAt(begin.LocalDateTime.AddHours(5), each));
        }

        var average = StatsPlanForecast.AveragePeriod(
            [points], WindowKind.Weekly, tokens, now.AddDays(-84), now, TimeZoneInfo.Local);

        Assert.NotNull(average);
        Assert.Equal(2, average!.PeriodCount);
        Assert.Equal(18_000, average.AverageTokens);
        Assert.Equal(30, average.AveragePeak, 6);
    }

    private static StatsRecord RecordAt(DateTime at, long tokens) =>
        new("claude", DateOnly.FromDateTime(at), Sonnet, "", tokens, 0, 0, 0, at.Hour);

    [Fact]
    public void Tokens_of_hours_without_a_reading_pair_are_scaled_to_the_percent_the_period_reached()
    {
        // One week: 10 000 tokens in an hour read at both ends (0 to 10 %), then 50 000 tokens in hours the
        // limit was not read for, ending at 30 %. The period cost 1 000 tokens per percent, so 30 000, not 60 000.
        var now = new DateTimeOffset(Start).AddDays(3);
        var begin = new DateTimeOffset(Start).AddDays(-7);
        var reset = begin.AddDays(7);
        List<HistoryPoint> points =
        [
            new(1, begin.AddHours(5), WindowKind.Weekly, 0, reset),
            new(1, begin.AddHours(6), WindowKind.Weekly, 10, reset),
            new(1, begin.AddHours(60), WindowKind.Weekly, 30, reset),
        ];
        var tokens = new[] { RecordAt(begin.LocalDateTime.AddHours(5), 10_000), RecordAt(begin.LocalDateTime.AddHours(30), 50_000) };

        var average = StatsPlanForecast.AveragePeriod([points], WindowKind.Weekly, tokens, now.AddDays(-84), now, TimeZoneInfo.Local);

        Assert.Equal(30_000, average!.AverageTokens);
        Assert.Equal(30, average.AveragePeak, 6);
    }

    [Fact]
    public void Without_any_hour_read_at_both_ends_there_is_no_average()
    {
        var now = new DateTimeOffset(Start).AddDays(3);
        var begin = new DateTimeOffset(Start).AddDays(-7);
        List<HistoryPoint> points =
        [
            new(1, begin.AddHours(1), WindowKind.Weekly, 0, begin.AddDays(7)),
            new(1, begin.AddHours(60), WindowKind.Weekly, 30, begin.AddDays(7)),
        ];
        var tokens = new[] { RecordAt(begin.LocalDateTime.AddHours(30), 50_000) };

        Assert.Null(StatsPlanForecast.AveragePeriod([points], WindowKind.Weekly, tokens, now.AddDays(-84), now, TimeZoneInfo.Local));
    }

    [Fact]
    public void A_period_that_started_before_the_lookback_or_has_not_ended_is_not_averaged()
    {
        var now = new DateTimeOffset(Start);
        var reset = now.AddDays(2);
        List<HistoryPoint> running = [new(1, now.AddHours(-3), WindowKind.Weekly, 30, reset)];
        var old = new List<HistoryPoint> { new(1, now.AddDays(-100), WindowKind.Weekly, 30, now.AddDays(-93)) };

        Assert.Null(StatsPlanForecast.AveragePeriod([running], WindowKind.Weekly, [], now.AddDays(-84), now, TimeZoneInfo.Local));
        Assert.Null(StatsPlanForecast.AveragePeriod([old], WindowKind.Weekly, [], now.AddDays(-84), now, TimeZoneInfo.Local));
    }

    [Fact]
    public void Pooled_accounts_split_the_tokens_of_their_shared_hours_between_their_periods()
    {
        var now = new DateTimeOffset(Start).AddDays(30);
        var begin = new DateTimeOffset(Start).AddDays(-14);
        List<HistoryPoint> Account(double peak) =>
            [new(1, begin.AddHours(5), WindowKind.Weekly, 0, begin.AddDays(7)), new(1, begin.AddHours(6), WindowKind.Weekly, peak, begin.AddDays(7))];
        var tokens = new[] { RecordAt(begin.LocalDateTime.AddHours(5), 20_000) };

        var average = StatsPlanForecast.AveragePeriod(
            [Account(10), Account(30)], WindowKind.Weekly, tokens, now.AddDays(-84), now, TimeZoneInfo.Local);

        Assert.Equal(2, average!.PeriodCount);
        Assert.Equal(10_000, average.AverageTokens);
        Assert.Equal(20, average.AveragePeak, 6);
    }
}
