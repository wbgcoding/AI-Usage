using System.Globalization;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Stats.Pricing;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public partial class MainViewModelTests
{
    private CostService PricedService(bool enabled = true)
    {
        var directory = TestPaths.CreateDisposableDirectory("tile-token-cost").Path;
        var service = new CostService(
            () => enabled, () => CostCurrency.Usd, directory, () => CultureInfo.GetCultureInfo("en-US"),
            () => new RegionInfo("US"), null, () => false);
        service.Prices.Replace(
            ModelPriceList.Parse("""{ "claude-sonnet-4-5": { "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015, "mode": "chat" } }"""),
            DateTimeOffset.UtcNow, persist: false);
        return service;
    }

    private MainViewModel BuildTileCost(
        out List<ProviderTileViewModel> tiles, bool tileCost, bool costEstimate = true, bool priced = true, bool withModelRow = false)
    {
        var vm = BuildWithStats(out tiles);
        _statsSettings!.ShowTileTokenCost = tileCost;
        _statsSettings.ShowCostEstimate = costEstimate;
        vm.Cost = PricedService(costEstimate);
        vm.WindowTokensQuery = (_, _, _) => 1_000_000;
        vm.WindowRecordsQuery = (provider, start) => priced
            ? [new StatsRecord(provider, DateOnly.FromDateTime(start), "claude-sonnet-4-5", "", 1_000_000, 0, 0, 0)]
            : [new StatsRecord(provider, DateOnly.FromDateTime(start), "mystery-model", "", 1_000_000, 0, 0, 0)];
        ApplyWindows(tiles, WeeklyReset, withModelRow);
        return vm;
    }

    [Fact]
    public async Task With_the_switch_on_the_token_text_of_a_main_window_ends_with_its_cost()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true);

        vm.RefreshWindowTokens(Now);

        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.All(row => row.InlineTokensText.EndsWith("~$3.00", StringComparison.Ordinal))));
        Assert.All(tiles, tile => Assert.All(tile.Rows, row => Assert.Contains(" · ~$3.00", row.InlineTokensText)));
    }

    [Fact]
    public async Task With_the_switch_off_the_token_text_has_no_cost()
    {
        var vm = BuildTileCost(out var tiles, tileCost: false);

        vm.RefreshWindowTokens(Now);

        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.All(row => row.WindowTokens == 1_000_000)));
        Assert.All(tiles, tile => Assert.All(tile.Rows, row =>
        {
            Assert.DoesNotContain("$", row.InlineTokensText);
            Assert.Equal("", row.WindowCost);
        }));
    }

    [Fact]
    public async Task Without_the_cost_estimate_the_tile_switch_alone_shows_nothing()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true, costEstimate: false);

        vm.RefreshWindowTokens(Now);

        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.All(row => row.WindowTokens == 1_000_000)));
        Assert.All(tiles, tile => Assert.All(tile.Rows, row => Assert.DoesNotContain("$", row.InlineTokensText)));
    }

    [Fact]
    public async Task Tokens_of_a_model_without_a_price_leave_the_text_unchanged()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true, priced: false);

        vm.RefreshWindowTokens(Now);

        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.All(row => row.WindowTokens == 1_000_000)));
        Assert.All(tiles, tile => Assert.All(tile.Rows, row => Assert.DoesNotContain("~", row.InlineTokensText)));
    }

    [Fact]
    public async Task A_window_without_a_reset_has_no_span_and_shows_no_cost()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true);
        foreach (var tile in tiles)
            tile.Apply(WindowsSnapshot(tile.RealProviderId, weeklyReset: null), Now);

        vm.RefreshWindowTokens(Now);

        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.Any(row => row.Kind == WindowKind.FiveHour && row.WindowCost.Length > 0)));
        Assert.All(tiles, tile =>
        {
            var weekly = tile.Rows.Single(row => row.Kind == WindowKind.Weekly);
            Assert.Null(weekly.WindowTokens);
            Assert.Equal("", weekly.WindowCost);
            Assert.DoesNotContain("$", weekly.InlineTokensText);
        });
    }

    [Fact]
    public async Task A_per_model_row_never_gets_a_cost()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true, withModelRow: true);

        vm.RefreshWindowTokens(Now);

        var claude = tiles.Single(tile => tile.ProviderId == "claude");
        await TestWait.UntilAsync(() => claude.Rows.Where(row => row.Kind != WindowKind.Other).All(row => row.WindowCost.Length > 0));
        var model = claude.Rows.Single(row => row.Kind == WindowKind.Other);
        Assert.Equal(1_000_000, model.WindowTokens);
        Assert.Equal("", model.WindowCost);
        Assert.DoesNotContain("$", model.InlineTokensText);
    }

    [Fact]
    public async Task Switching_the_tile_cost_off_takes_the_figure_back_at_once()
    {
        var vm = BuildTileCost(out var tiles, tileCost: true);
        vm.RefreshWindowTokens(Now);
        await TestWait.UntilAsync(() => tiles.All(tile => tile.Rows.All(row => row.WindowCost.Length > 0)));

        _statsSettings!.ShowTileTokenCost = false;
        vm.CostSettingsChanged();

        Assert.All(tiles, tile => Assert.All(tile.Rows, row => Assert.DoesNotContain("$", row.InlineTokensText)));
    }
}
