using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>How the project grouping names its bars and rows.</summary>
public class StatsViewModelProjectBarsTests
{
    private static StatsViewModel Create(params string[] projects) => Create(_ => true, projects);

    private static StatsViewModel Create(Func<string, bool> directoryExists, params string[] projects)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-project-bars");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([.. projects.Select((project, index) =>
            new StatsRecord("claude", today, "claude-sonnet-4-5", project, 1000 - index, 0, 0, 0))]);
        var viewModel = new StatsViewModel(store);
        viewModel.BuildAliases = (records, sessions) => ProjectAliasResolver.Build(records, sessions, directoryExists);
        viewModel.SelectedGrouping = StatsGrouping.Project;
        viewModel.Recompute();
        return viewModel;
    }

    [Fact]
    public void Every_project_bar_is_named_by_its_folder_and_keeps_the_full_path_for_the_tooltip()
    {
        var viewModel = Create(@"E:\Work\AI-Usage", @"D:\work\Tool\");

        Assert.Equal(["AI-Usage", "Tool"], viewModel.Bars.Select(bar => bar.Label));
        Assert.Equal([@"E:\Work\AI-Usage", @"D:\work\Tool"], viewModel.Bars.Select(bar => bar.TooltipTitle));
        Assert.Equal(["AI-Usage", "Tool"], viewModel.Rows.Select(row => row.Label));
    }

    [Fact]
    public void Two_projects_with_the_same_folder_name_show_their_parent_folder()
    {
        var viewModel = Create(@"E:\Work\AI-Usage", @"E:\Other\AI-Usage");

        Assert.Equal(["AI-Usage (Work)", "AI-Usage (Other)"], viewModel.Bars.Select(bar => bar.Label));
        Assert.Equal(["AI-Usage (Work)", "AI-Usage (Other)"], viewModel.Rows.Select(row => row.Label));
    }

    [Fact]
    public void A_moved_project_is_one_bar_whose_tooltip_names_the_old_folder()
    {
        var viewModel = Create(path => path == @"E:\Work\AI-Usage", @"E:\Work\AI-Usage", @"C:\old\AI-Usage");

        var bar = Assert.Single(viewModel.Bars);
        Assert.Equal(@"E:\Work\AI-Usage", bar.TooltipTitle);
        Assert.Contains(string.Format(LocalizationService.Instance["Stats.Project.Tip.FormerPaths"], @"C:\old\AI-Usage"), bar.ExtraTooltipLine);
        Assert.Equal(1999, Assert.Single(viewModel.Rows).TotalTokens);
    }
}
