using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>The plan forecast section of the statistics view model: which providers it shows, the
/// heading with plans and accounts, the too-few-data line and the provider filter.</summary>
public class StatsForecastViewModelTests
{
    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>Hourly claude tokens and a weekly quota that rises with them, one reading per hour.</summary>
    private static (List<StatsRecord> Records, List<HistoryPoint> Points) History(int hours, string provider = "claude")
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
            records.Add(new StatsRecord(provider, DateOnly.FromDateTime(moment), "claude-opus-4-8", "p", tokens, 0, 0, 0, moment.Hour));
            percent += tokens / 2e6;
        }

        points.Add(new HistoryPoint(1, new DateTimeOffset(start.AddHours(hours)), WindowKind.Weekly, percent, reset));
        return (records, points);
    }

    private static async Task<(StatsViewModel ViewModel, IDisposable Directory)> CreateAsync(
        int hours, Func<string, IReadOnlyList<string>>? accountKeys = null, Func<string, string?>? plan = null,
        Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>>? history = null)
    {
        var directory = TestPaths.CreateDisposableDirectory("stats-forecast");
        var store = new StatsStore(directory);
        var (records, points) = History(hours);
        store.AddDelta(records);
        var viewModel = new StatsViewModel(store)
        {
            LoadHistory = history ?? ((key, _, _) => key.StartsWith("claude", StringComparison.Ordinal) ? points : []),
        };
        if (accountKeys is not null)
            viewModel.AccountKeysOf = accountKeys;
        if (plan is not null)
            viewModel.PlanOf = plan;
        viewModel.Recompute();
        await viewModel.ForecastRefreshForTest;
        return (viewModel, directory);
    }

    [Fact]
    public async Task A_provider_with_history_and_tokens_gets_a_group_with_its_plan_in_the_heading()
    {
        var (viewModel, directory) = await CreateAsync(100, plan: _ => "Max 5x");
        using (directory)
        {
            var group = Assert.Single(viewModel.ForecastGroups);
            Assert.True(viewModel.HasForecast);
            Assert.Equal("claude", group.ProviderId);
            Assert.Equal("Claude · Max 5x", group.Heading);
            Assert.Contains(group.Rows, row => row.Text.StartsWith(Loc.Format("Stats.Forecast.Week", "").TrimEnd(), StringComparison.Ordinal)
                || row.Text.Contains('≈', StringComparison.Ordinal));
            Assert.All(group.Rows, row => Assert.False(string.IsNullOrEmpty(row.ToolTip)));
        }
    }

    [Fact]
    public async Task A_provider_without_a_known_plan_shows_only_its_name()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
            Assert.Equal("Claude", Assert.Single(viewModel.ForecastGroups).Heading);
    }

    [Fact]
    public async Task Several_accounts_are_pooled_into_one_group_with_all_plans_and_the_count()
    {
        var plans = new Dictionary<string, string?> { ["claude"] = "Max 5x", ["claude#2"] = "Pro" };
        var (viewModel, directory) = await CreateAsync(
            100, id => id == "claude" ? ["claude", "claude#2"] : [id], key => plans.GetValueOrDefault(key));
        using (directory)
        {
            var group = Assert.Single(viewModel.ForecastGroups);
            Assert.Equal("Claude · Max 5x + Pro (" + Loc.Format("Stats.Forecast.Accounts", 2) + ")", group.Heading);
        }
    }

    [Fact]
    public async Task Too_few_hours_give_the_single_not_enough_data_line()
    {
        var (viewModel, directory) = await CreateAsync(8);
        using (directory)
        {
            var group = Assert.Single(viewModel.ForecastGroups);
            var row = Assert.Single(group.Rows);
            Assert.Equal(Loc["Stats.Forecast.TooFew"], row.Text);
        }
    }

    [Fact]
    public async Task No_quota_history_hides_the_section()
    {
        var (viewModel, directory) = await CreateAsync(100, history: (_, _, _) => []);
        using (directory)
        {
            Assert.Empty(viewModel.ForecastGroups);
            Assert.False(viewModel.HasForecast);
        }
    }

    [Fact]
    public async Task The_provider_filter_applies_and_hides_the_section_when_it_excludes_every_forecast()
    {
        var (viewModel, directory) = await CreateAsync(100);
        using (directory)
        {
            viewModel.SetProviderCommand.Execute("codex");
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
            var before = Assert.Single(viewModel.ForecastGroups).Rows.Select(row => row.Text).ToList();
            await viewModel.SetRangeCommand.ExecuteAsync("ThisMonth");
            await viewModel.ForecastRefreshForTest;
            Assert.Equal(before, Assert.Single(viewModel.ForecastGroups).Rows.Select(row => row.Text));
        }
    }

    [Fact]
    public async Task An_account_key_seam_that_throws_falls_back_to_the_providers_own_history()
    {
        var (viewModel, directory) = await CreateAsync(100, id => throw new InvalidOperationException("collection modified"));
        using (directory)
            Assert.Single(viewModel.ForecastGroups);
    }
}
