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

    private static StatsViewModel Create(
        CostService? cost, Action<StatsViewModel>? configure = null, Func<string, string?>? save = null, IEnumerable<StatsRecord>? extra = null)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-cost-store");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "claude-sonnet-4-5", "projA", 1_000_000, 0, 0, 0),
            new StatsRecord("claude", today, "mystery-model", "projB", 500, 500, 0, 0),
            .. extra ?? [],
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
        Assert.Equal("~$3.00", claude.CostText);
        Assert.Contains("~$3.00", claude.LabelValueText);
        Assert.DoesNotContain("*", claude.LabelValueText);
    }

    [Fact]
    public void The_provider_row_tooltip_adds_the_cost_and_the_unpriced_models_to_the_exact_tokens()
    {
        var viewModel = Create(Service());
        var claude = viewModel.ProviderRows.First(row => row.CostText.Length > 0);

        var lines = claude.TooltipText.Split('\n');
        Assert.Equal(claude.ExactText, lines[0]);
        Assert.Equal(AiUsage.Services.LocalizationService.Instance["Stats.Cost.Column"] + " ~$3.00*", lines[1]);
        Assert.Contains("mystery-model", lines[2]);
    }

    [Fact]
    public void The_provider_row_tooltip_is_only_the_exact_tokens_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        Assert.All(viewModel.ProviderRows, row => Assert.Equal(row.ExactText, row.TooltipText));
    }

    [Fact]
    public void A_heatmap_cell_names_its_cost_and_the_unpriced_models_and_an_empty_cell_none()
    {
        var viewModel = Create(Service());

        Assert.Equal(168, viewModel.HeatmapCostLines.Count);
        var filled = viewModel.HeatmapCostLines.Where(line => line.Length > 0).ToList();
        var line = Assert.Single(filled);
        Assert.Contains("~$3.00*", line);
        Assert.Contains("mystery-model", line);
    }

    [Fact]
    public void The_heatmap_carries_no_cost_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        Assert.All(viewModel.HeatmapCostLines, line => Assert.Equal("", line));
    }

    [Fact]
    public void A_model_bar_tooltip_carries_the_cost_of_that_model()
    {
        var viewModel = Create(Service(), vm => vm.SelectedGrouping = StatsGrouping.Model);
        var priced = Assert.Single(viewModel.Bars, bar => bar.Label.Contains("Sonnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("~$3.00", priced.ExtraTooltipLine);
        Assert.Contains(AiUsage.Services.LocalizationService.Instance["Stats.Cost.Column"] + " ~$3.00", priced.ExtraTooltipLine);
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

    private static StatsViewModel CreateForExport(CostService cost, out string path)
    {
        var target = Path.Combine(TestPaths.CreateDisposableDirectory("stats-cost-csv").Path, "out.csv");
        path = target;
        return Create(cost, save: _ => target);
    }

    [Fact]
    public void The_export_is_one_csv_with_every_section_and_the_same_column_count_per_row()
    {
        var viewModel = CreateForExport(Service(), out var path);
        viewModel.ExportCsvCommand.Execute(null);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var rows = StatsExportTests.Parse(File.ReadAllText(path));
        Assert.All(rows, cells => Assert.Equal(17, cells.Count));
        var sections = rows.Skip(1).Select(cells => cells[0]).ToHashSet();
        foreach (var section in new[]
            { "total", "by_provider", "by_model", "by_project", "by_effort", "by_day", "by_hour", "by_weekday_hour",
              "top_projects", "cache_split", "figures", "rows" })
            Assert.Contains(section, sections);
    }

    [Fact]
    public void The_days_of_the_export_add_up_to_the_total()
    {
        var viewModel = CreateForExport(Service(), out var path);
        viewModel.ExportCsvCommand.Execute(null);

        var rows = StatsExportTests.Parse(File.ReadAllText(path)).Skip(1).ToList();
        var total = long.Parse(rows.Single(cells => cells[0] == "total")[13]);
        Assert.Equal(1_001_000, total);
        Assert.Equal(total, rows.Where(cells => cells[0] == "by_day").Sum(cells => long.Parse(cells[13])));
        Assert.Equal(total, rows.Where(cells => cells[0] == "by_model").Sum(cells => long.Parse(cells[13])));
        Assert.Equal(total, rows.Where(cells => cells[0] == "rows").Sum(cells => long.Parse(cells[13])));
    }

    [Fact]
    public void The_export_has_a_dollar_amount_and_the_shown_amount_and_leaves_an_unpriced_model_empty()
    {
        var viewModel = CreateForExport(Service(), out var path);
        viewModel.ExportCsvCommand.Execute(null);

        var rows = StatsExportTests.Parse(File.ReadAllText(path)).Skip(1).ToList();
        var priced = rows.Single(cells => cells[0] == "by_model" && cells[5] == "claude-sonnet-4-5");
        Assert.Equal("3", priced[14]);
        Assert.Equal("3", priced[15]);
        Assert.Equal("USD", priced[16]);
        var mystery = rows.Single(cells => cells[0] == "by_model" && cells[5] == "mystery-model");
        Assert.Equal(new[] { "", "", "" }, mystery.Skip(14).ToArray());
    }

    [Fact]
    public void The_export_leaves_the_cost_columns_empty_when_the_estimate_is_off()
    {
        var viewModel = CreateForExport(Service(enabled: false), out var path);
        viewModel.ExportCsvCommand.Execute(null);

        var rows = StatsExportTests.Parse(File.ReadAllText(path)).Skip(1).ToList();
        Assert.All(rows, cells => Assert.Equal(new[] { "", "", "" }, cells.Skip(14).ToArray()));
    }

    [Fact]
    public void The_rows_section_keeps_the_raw_project_and_adds_the_merged_one()
    {
        var viewModel = CreateForExport(Service(), out var path);
        viewModel.ExportCsvCommand.Execute(null);

        var rows = StatsExportTests.Parse(File.ReadAllText(path)).Where(cells => cells[0] == "rows").ToList();
        Assert.Contains(rows, cells => cells[6] == "projA" && cells[7] == "projA");
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
    public void The_ring_slices_carry_a_short_cost_without_a_star_and_none_for_an_unpriced_model()
    {
        var viewModel = Create(Service());

        Assert.Equal("~$3.00", Assert.Single(viewModel.ProviderShareSlices, slice => slice.ProviderId == "claude").CostText);
        Assert.Contains(viewModel.ModelShareSlices, slice => slice.CostText == "~$3.00");
        Assert.Contains(viewModel.ModelShareSlices, slice => string.IsNullOrEmpty(slice.CostText));
        Assert.All(viewModel.ModelShareSlices, slice => Assert.DoesNotContain("*", slice.CostText ?? ""));
    }

    [Fact]
    public void A_top_project_row_carries_a_short_cost_without_a_star_and_none_when_unpriced()
    {
        var viewModel = Create(Service());

        Assert.Equal("~$3.00", viewModel.TopProjectRows.Single(row => row.FullPath == "projA").CostText);
        Assert.True(string.IsNullOrEmpty(viewModel.TopProjectRows.Single(row => row.FullPath == "projB").CostText));
    }

    [Fact]
    public void A_top_project_row_carries_no_cost_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        Assert.All(viewModel.TopProjectRows, row => Assert.True(string.IsNullOrEmpty(row.CostText)));
    }

    [Fact]
    public void The_cache_share_tooltip_names_tokens_and_cost_per_part_and_the_unpriced_models()
    {
        var viewModel = Create(Service());

        var lines = viewModel.CacheShareTooltip.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.EndsWith(" \u00B7 ~$3.00", lines[0]);
        Assert.StartsWith(viewModel.CacheShareLabels[0] + ": ", lines[0]);
        Assert.DoesNotContain("\u00B7", lines[1]);
        Assert.DoesNotContain("\u00B7", lines[2]);
        Assert.StartsWith(viewModel.CacheShareLabels[3] + ": ", lines[3]);
        Assert.Contains("mystery-model", lines[4]);
    }

    [Fact]
    public void The_cache_share_tooltip_names_only_the_tokens_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        var lines = viewModel.CacheShareTooltip.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.All(lines, line => Assert.DoesNotContain("\u00B7", line));
    }

    [Fact]
    public void The_ring_slices_carry_no_cost_when_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        Assert.All(viewModel.ProviderShareSlices, slice => Assert.True(string.IsNullOrEmpty(slice.CostText)));
        Assert.All(viewModel.ModelShareSlices, slice => Assert.True(string.IsNullOrEmpty(slice.CostText)));
        Assert.All(viewModel.EffortShareSlices, slice => Assert.True(string.IsNullOrEmpty(slice.CostText)));
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

    [Fact]
    public void Every_token_figure_of_the_figures_section_adds_its_cost()
    {
        var viewModel = Create(Service());

        // One active day holds all of it, so the busiest day and the active-day average equal the total.
        Assert.Equal("~$3.00*", viewModel.BusiestDayCostText);
        Assert.Equal("~$3.00*", viewModel.PerActiveDayCostText);
        Assert.StartsWith("~$", viewModel.PerDayCostText);
        Assert.EndsWith("*", viewModel.PerDayCostText);
    }

    [Fact]
    public void The_previous_period_figure_names_its_cost()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var earlier = Enumerable.Range(0, 20).Select(offset =>
            new StatsRecord("claude", today.AddDays(-35 - offset), "claude-sonnet-4-5", "projA", 2_000_000, 0, 0, 0));
        var viewModel = Create(Service(), vm => vm.SelectedRange = "Month", extra: earlier);

        Assert.True(viewModel.HasPreviousPeriod);
        Assert.Contains("~$", viewModel.VsPreviousCaption);
    }

    [Fact]
    public void The_figures_section_shows_no_cost_while_the_estimate_is_off()
    {
        var viewModel = Create(Service(enabled: false));

        Assert.Equal("", viewModel.PerDayCostText);
        Assert.Equal("", viewModel.PerActiveDayCostText);
        Assert.Equal("", viewModel.BusiestDayCostText);
        Assert.DoesNotContain("~", viewModel.VsPreviousCaption);
    }
}
