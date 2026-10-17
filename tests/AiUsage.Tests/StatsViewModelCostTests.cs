using System.Globalization;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;

namespace AiUsage.Tests;

/// <summary>The cost estimate as the statistics view model shows it: header, provider rows, bar
/// tooltips, sessions and the export.</summary>
public class StatsViewModelCostTests
{
    private const string PriceJson = """
    {
      "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "mode": "chat" }
    }
    """;

    private static CostService Service(bool enabled = true, bool withPrices = true)
    {
        var directory = TestPaths.CreateDisposableDirectory("stats-cost").Path;
        var service = new CostService(
            () => enabled, () => CostCurrency.Usd, directory, () => CultureInfo.GetCultureInfo("en-US"),
            () => new RegionInfo("US"), null, () => false);
        if (withPrices)
            service.Prices.Replace(ModelPriceList.Parse(PriceJson), DateTimeOffset.UtcNow, persist: false);
        return service;
    }

    private static StatsViewModel Create(CostService? cost, Action<StatsViewModel>? configure = null, Func<string, string?>? save = null)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-cost-store");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "claude-sonnet-4-5", "projA", 1_000_000, 0, 0, 0),
            new StatsRecord("claude", today, "mystery-model", "projB", 500, 500, 0, 0),
        ]);
        var viewModel = new StatsViewModel(store, save, _ => { }, cost: cost);
        configure?.Invoke(viewModel);
        viewModel.Recompute();
        return viewModel;
    }

    [Fact]
    public void The_header_shows_the_priced_part_with_a_star_for_an_unpriced_model()
    {
        var viewModel = Create(Service());
        Assert.True(viewModel.HasTotalCost);
        Assert.Equal("~$3.00*", viewModel.TotalCostText);
        Assert.Contains("mystery-model", viewModel.TotalCostTooltip);
    }

    [Fact]
    public void Off_means_no_cost_anywhere()
    {
        var viewModel = Create(Service(enabled: false), vm => vm.SelectedGrouping = StatsGrouping.Model);
        Assert.False(viewModel.HasTotalCost);
        Assert.Equal("", viewModel.TotalCostText);
        Assert.All(viewModel.ProviderRows, row => Assert.Equal("", row.CostText));
        Assert.All(viewModel.Bars, bar => Assert.True(string.IsNullOrEmpty(bar.ExtraTooltipLine)));
    }

    [Fact]
    public void Before_the_prices_are_loaded_nothing_is_shown()
    {
        var viewModel = Create(Service(withPrices: false));
        Assert.False(viewModel.HasTotalCost);
        Assert.All(viewModel.ProviderRows, row => Assert.Equal("", row.CostText));
    }

    [Fact]
    public void Without_a_service_the_view_model_shows_no_cost()
    {
        var viewModel = Create(null);
        Assert.False(viewModel.HasTotalCost);
    }

    [Fact]
    public void The_provider_row_carries_its_cost()
    {
        var viewModel = Create(Service());
        var claude = viewModel.ProviderRows.First(row => row.CostText.Length > 0);
        Assert.Equal("~$3.00*", claude.CostText);
        Assert.Contains("~$3.00*", claude.LabelValueText);
    }

    [Fact]
    public void A_model_bar_tooltip_carries_the_cost_of_that_model()
    {
        var viewModel = Create(Service(), vm => vm.SelectedGrouping = StatsGrouping.Model);
        var priced = Assert.Single(viewModel.Bars, bar => bar.Label.Contains("Sonnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("~$3.00", priced.ExtraTooltipLine);
        var unpriced = Assert.Single(viewModel.Bars, bar => bar.Label.Contains("mystery", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("?*", unpriced.ExtraTooltipLine);
    }

    [Fact]
    public void A_day_bar_tooltip_carries_the_cost_of_that_day()
    {
        var viewModel = Create(Service(), vm => vm.SelectedGrouping = StatsGrouping.Day);
        Assert.Contains(viewModel.Bars, bar => bar.ExtraTooltipLine?.Contains("~$3.00*") == true);
    }

    [Fact]
    public void A_project_bar_tooltip_carries_the_cost_of_that_project()
    {
        var viewModel = Create(Service(), vm => vm.SelectedGrouping = StatsGrouping.Project);
        Assert.Contains(viewModel.Bars, bar => bar.ExtraTooltipLine?.Contains("~$3.00") == true);
    }

    [Fact]
    public void The_export_has_a_plain_dollar_column_and_empty_for_unknown()
    {
        string? written = null;
        var path = Path.Combine(TestPaths.CreateDisposableDirectory("stats-cost-csv").Path, "out.csv");
        var viewModel = Create(Service(), vm => vm.SelectedGrouping = StatsGrouping.Model, _ => path);
        viewModel.ExportCsvCommand.Execute(null);
        written = File.ReadAllText(path);

        var lines = written.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",cost_usd", lines[0]);
        Assert.Contains(lines, line => line.EndsWith(",3", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("mystery", StringComparison.OrdinalIgnoreCase) && line.EndsWith(",", StringComparison.Ordinal));
    }

    [Fact]
    public void The_export_has_no_cost_column_when_the_estimate_is_off()
    {
        var path = Path.Combine(TestPaths.CreateDisposableDirectory("stats-cost-csv-off").Path, "out.csv");
        var viewModel = Create(Service(enabled: false), vm => vm.SelectedGrouping = StatsGrouping.Model, _ => path);
        viewModel.ExportCsvCommand.Execute(null);
        var lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("cost_usd", lines[0]);
    }

    [Fact]
    public void A_finished_download_redraws_the_figures()
    {
        var service = Service(withPrices: false);
        var viewModel = Create(service);
        Assert.False(viewModel.HasTotalCost);

        service.Prices.Replace(ModelPriceList.Parse(PriceJson), DateTimeOffset.UtcNow, persist: false);
        viewModel.RefreshCost();
        Assert.True(viewModel.HasTotalCost);
    }

    [Fact]
    public void The_ring_tooltips_and_the_month_grid_carry_the_cost()
    {
        var viewModel = Create(Service());
        Assert.Contains(viewModel.ProviderShareSlices, slice => slice.Tooltip?.Contains("~$3.00*") == true);
        Assert.Contains(viewModel.ModelShareSlices, slice => slice.Tooltip?.Contains("~$3.00") == true);
        Assert.Contains(viewModel.MonthGridDays, day => day.CostLine?.Contains("~$3.00*") == true);
    }

    [Fact]
    public void Nothing_is_added_to_the_rings_or_the_month_grid_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));
        Assert.All(viewModel.ProviderShareSlices, slice => Assert.DoesNotContain("~", slice.Tooltip ?? ""));
        Assert.All(viewModel.MonthGridDays, day => Assert.Null(day.CostLine));
    }

    [Fact]
    public async Task A_session_row_shows_what_its_main_model_costs_and_a_question_mark_for_an_unknown_one()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-cost-sessions");
        var store = new StatsStore(dataDir);
        var noon = DateTimeOffset.UtcNow.Date.AddHours(12);
        store.ApplyIndexResult(
            [], new StatsSourceFileState("seed-cost", "claude", 0, 0, DateTime.UtcNow),
            [
                new StatsSessionDelta("claude", "priced", "C:/p/a", noon, noon.AddMinutes(5), 1_000_000, 0, 0, 0, 0,
                    new Dictionary<string, long> { ["claude-sonnet-4-5"] = 1_000_000 }),
                new StatsSessionDelta("claude", "unknown", "C:/p/b", noon, noon.AddMinutes(5), 500, 0, 0, 0, 0,
                    new Dictionary<string, long> { ["mystery-model"] = 500 }),
            ]);
        var viewModel = StatsVm.Create(store, cost: Service());
        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Session);

        Assert.Contains(viewModel.SessionRows, row => row.CostText == "~$3.00");
        Assert.Contains(viewModel.SessionRows, row => row.CostText == "?");
        Assert.Contains(viewModel.SessionRows, row => row.ToolTip.Contains("~$3.00"));
    }
}
