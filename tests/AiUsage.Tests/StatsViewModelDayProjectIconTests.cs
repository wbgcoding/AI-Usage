using System.Windows.Media;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The project rows of the day detail carry the same color and icon as the top projects.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsViewModelDayProjectIconTests
{
    private static readonly Color Orange = Color.FromRgb(200, 100, 20);

    [Fact]
    public async Task The_day_detail_project_rows_carry_the_icon_of_their_project()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-day-icons");
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        store.AddDelta([
            new StatsRecord("claude", day, "modelA", "C:/Projects/One", 500, 0, 0, 0),
            new StatsRecord("claude", day, "modelA", "C:/Projects/Two", 300, 0, 0, 0),
        ]);
        var viewModel = new StatsViewModel(store)
        {
            ProjectColorResolve = (folders, _) => folders.ToDictionary(
                folder => folder,
                folder => new ProjectColorInfo(Orange, folder.EndsWith("One", StringComparison.OrdinalIgnoreCase) ? "C:/icons/one.png" : null)),
        };
        viewModel.Recompute();
        await viewModel.LastProjectColorRefresh;

        viewModel.SelectDay(day);
        await viewModel.LastProjectColorRefresh;

        var rows = viewModel.SelectedDayDetail.ProjectRows;
        Assert.Equal("C:/icons/one.png", rows.Single(row => row.FullPath.EndsWith("One", StringComparison.OrdinalIgnoreCase)).IconPath);
        Assert.Null(rows.Single(row => row.FullPath.EndsWith("Two", StringComparison.OrdinalIgnoreCase)).IconPath);
        Assert.All(rows, row => Assert.Equal(Orange, row.Color));
    }

    [Fact]
    public async Task A_project_spelled_in_two_cases_shares_one_color_and_icon_between_both_views()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-day-icons-case");
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        store.AddDelta([
            new StatsRecord("claude", day, "modelA", "C:/Projects/One", 500, 0, 0, 0),
            new StatsRecord("claude", day.AddDays(-2), "modelA", "c:/projects/one", 300, 0, 0, 0),
        ]);
        var viewModel = new StatsViewModel(store)
        {
            ProjectColorResolve = (folders, _) =>
                folders.ToDictionary(folder => folder, _ => new ProjectColorInfo(Orange, "C:/icons/one.png")),
        };
        viewModel.Recompute();
        await viewModel.LastProjectColorRefresh;
        viewModel.SelectDay(day);
        await viewModel.LastProjectColorRefresh;

        Assert.All(viewModel.TopProjectRows, row => Assert.Equal("C:/icons/one.png", row.IconPath));
        Assert.All(viewModel.SelectedDayDetail.ProjectRows, row => Assert.Equal("C:/icons/one.png", row.IconPath));
        Assert.Single(viewModel.ProjectColors.Keys.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToDayProjectRows_sets_the_icon_of_a_project_and_none_for_the_pooled_row()
    {
        var day = new DateOnly(2026, 5, 1);
        var slices = new List<StatsShareSlice>
        {
            new("C:/Projects/One", 500, 0.5),
            new("Other", 500, 0.5, IsPooled: true),
        };
        var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase) { ["c:/projects/one"] = Orange };
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["c:/projects/one"] = "C:/icons/one.png" };

        var rows = StatsViewModel.ToDayProjectRows(slices, day, colors, icons);

        Assert.Equal("C:/icons/one.png", rows[0].IconPath);
        Assert.Equal(Orange, rows[0].Color);
        Assert.Null(rows[1].IconPath);
    }
}
