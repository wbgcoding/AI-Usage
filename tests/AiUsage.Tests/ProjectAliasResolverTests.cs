using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Projects that moved to another folder continue as one: the alias is worked out at query
/// time from the stored paths and never rewrites the index.</summary>
public class ProjectAliasResolverTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    private static StatsRecord Record(string project, int dayOffset = 0, int hour = 0, long tokens = 100) =>
        new("claude", Day.AddDays(dayOffset), "claude-sonnet-4-5", project, tokens, 0, 0, 0, hour);

    private static ProjectAliases Build(IEnumerable<string> existing, params StatsRecord[] records)
    {
        var present = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return ProjectAliasResolver.Build(records, [], path => present.Contains(path));
    }

    [Fact]
    public void A_path_that_is_gone_merges_into_the_one_that_exists()
    {
        var aliases = Build([@"E:\Work\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Work\AI-Usage"));

        Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage"));
        Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(@"E:\Work\AI-Usage"));
        Assert.Equal([@"C:\a\AI-Usage"], aliases.FormerPaths(@"E:\Work\AI-Usage"));
    }

    [Fact]
    public void When_both_exist_the_projects_stay_two()
    {
        var aliases = Build([@"C:\a\AI-Usage", @"E:\Work\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Work\AI-Usage"));

        Assert.Equal(@"C:\a\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage"));
        Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(@"E:\Work\AI-Usage"));
        Assert.Empty(aliases.FormerPaths(@"E:\Work\AI-Usage"));
    }

    [Fact]
    public void When_none_exists_the_most_recently_used_path_wins()
    {
        var aliases = Build([], Record(@"C:\a\AI-Usage", dayOffset: 0), Record(@"E:\Work\AI-Usage", dayOffset: 5), Record(@"D:\b\AI-Usage", dayOffset: 2));

        Assert.All([@"C:\a\AI-Usage", @"D:\b\AI-Usage", @"E:\Work\AI-Usage"],
            path => Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(path)));
        Assert.Equal(2, aliases.FormerPaths(@"E:\Work\AI-Usage").Count);
    }

    [Fact]
    public void The_hour_breaks_a_tie_on_the_same_day()
    {
        var aliases = Build([], Record(@"C:\a\Tool", hour: 3), Record(@"D:\b\Tool", hour: 9));

        Assert.Equal(@"D:\b\Tool", aliases.Resolve(@"C:\a\Tool"));
    }

    [Fact]
    public void A_session_counts_as_use_of_its_project()
    {
        var present = new HashSet<string>();
        var session = new StatsSessionRecord(
            "claude", "", "s1", @"C:\a\Tool", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
            1, 0, 0, 0, "claude-sonnet-4-5", 0);

        var aliases = ProjectAliasResolver.Build([Record(@"D:\b\Tool", dayOffset: 3)], [session], present.Contains);

        Assert.Equal(@"C:\a\Tool", aliases.Resolve(@"D:\b\Tool"));
    }

    [Fact]
    public void Different_folder_names_and_bare_names_never_merge()
    {
        var aliases = Build([], Record(@"C:\a\Alpha"), Record(@"C:\a\Beta"), Record("Gamma"), Record(""));

        Assert.Equal(@"C:\a\Alpha", aliases.Resolve(@"C:\a\Alpha"));
        Assert.Equal("Gamma", aliases.Resolve("Gamma"));
        Assert.Equal("", aliases.Resolve(""));
    }

    [Fact]
    public void The_same_folder_in_another_letter_case_is_one_path_not_two_that_exist()
    {
        var aliases = Build([@"E:\Work\AI-Usage"], Record(@"e:\work\ai-usage"), Record(@"E:\Work\AI-Usage"), Record(@"C:\gone\AI-Usage"));

        Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(@"C:\gone\AI-Usage"), ignoreCase: true);
    }

    [Fact]
    public void An_agent_work_folder_resolves_by_its_project_root()
    {
        var aliases = Build([@"E:\Work\AI-Usage"], Record(@"C:\a\AI-Usage\.tmp\wt-x"), Record(@"E:\Work\AI-Usage"));

        Assert.Equal(@"E:\Work\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage\.tmp\wt-x"));
    }

    [Fact]
    public void Each_path_is_checked_once_per_load()
    {
        var calls = new List<string>();
        var records = new[] { Record(@"C:\a\Tool"), Record(@"C:\a\Tool", dayOffset: 1), Record(@"D:\b\Tool") };

        ProjectAliasResolver.Build(records, [], path => { calls.Add(path); return false; });

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Unrelated_subfolders_of_one_name_stay_apart()
    {
        var aliases = Build(
            [@"E:\Dev\Maze\review"],
            Record(@"C:\Dev\AI-Usage"), Record(@"C:\Dev\AI-Usage\review"), Record(@"C:\Dev\Note Keeper"), Record(@"C:\Dev\Note Keeper\review"),
            Record(@"E:\Dev\Maze"), Record(@"E:\Dev\Maze\review"));

        Assert.Equal(@"C:\Dev\AI-Usage\review", aliases.Resolve(@"C:\Dev\AI-Usage\review"));
        Assert.Equal(@"C:\Dev\Note Keeper\review", aliases.Resolve(@"C:\Dev\Note Keeper\review"));
        Assert.Equal(@"E:\Dev\Maze\review", aliases.Resolve(@"E:\Dev\Maze\review"));
        Assert.Empty(aliases.FormerPaths(@"E:\Dev\Maze\review"));
    }

    [Fact]
    public void A_subfolder_of_a_moved_project_follows_the_move()
    {
        var aliases = Build(
            [@"E:\Dev\Note Keeper", @"E:\Dev\Note Keeper\review"],
            Record(@"C:\Dev\Note Keeper"), Record(@"E:\Dev\Note Keeper"),
            Record(@"C:\Dev\Note Keeper\review"), Record(@"E:\Dev\Note Keeper\review"));

        Assert.Equal(@"E:\Dev\Note Keeper", aliases.Resolve(@"C:\Dev\Note Keeper"));
        Assert.Equal(@"E:\Dev\Note Keeper\review", aliases.Resolve(@"C:\Dev\Note Keeper\review"));
    }

    [Fact]
    public void A_subfolder_name_under_two_different_projects_stays_two()
    {
        var aliases = Build(
            [],
            Record(@"C:\Dev\Messenger"), Record(@"C:\Dev\Messenger\android", dayOffset: 1),
            Record(@"C:\Dev\Scanner"), Record(@"C:\Dev\Scanner\android", dayOffset: 2));

        Assert.Equal(@"C:\Dev\Messenger\android", aliases.Resolve(@"C:\Dev\Messenger\android"));
        Assert.Equal(@"C:\Dev\Scanner\android", aliases.Resolve(@"C:\Dev\Scanner\android"));
    }

    [Fact]
    public void A_subfolder_and_a_project_of_the_same_name_stay_apart()
    {
        var aliases = Build(
            [@"E:\Dev\Tool"],
            Record(@"C:\Dev\Other"), Record(@"C:\Dev\Other\Tool"), Record(@"E:\Dev\Tool"));

        Assert.Equal(@"C:\Dev\Other\Tool", aliases.Resolve(@"C:\Dev\Other\Tool"));
    }

    [Fact]
    public void A_bare_name_follows_the_merged_project()
    {
        var aliases = Build([@"E:\Dev\Note Keeper"], Record(@"C:\Dev\Note Keeper"), Record(@"E:\Dev\Note Keeper"), Record("Note Keeper"));

        Assert.Equal(@"E:\Dev\Note Keeper", aliases.Resolve("Note Keeper"));
        Assert.Equal(@"E:\Dev\Note Keeper", aliases.Resolve("note keeper"));
        Assert.Equal([@"C:\Dev\Note Keeper"], aliases.FormerPaths(@"E:\Dev\Note Keeper"));
    }

    [Fact]
    public void A_bare_session_name_resolves_to_its_project()
    {
        var session = new StatsSessionRecord(
            "codex", "", "s1", "Note Keeper", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
            1, 0, 0, 0, "gpt-5", 0);
        var present = new HashSet<string> { @"E:\Dev\Note Keeper" };

        var aliases = ProjectAliasResolver.Build(
            [Record(@"C:\Dev\Note Keeper"), Record(@"E:\Dev\Note Keeper")], [session], present.Contains);

        Assert.Equal(@"E:\Dev\Note Keeper", aliases.Apply([session])[0].Project);
    }

    [Fact]
    public void A_bare_name_stays_when_two_projects_of_that_name_exist()
    {
        var aliases = Build([@"C:\a\Tool", @"E:\Work\Tool"], Record(@"C:\a\Tool"), Record(@"E:\Work\Tool"), Record("Tool"));

        Assert.Equal("Tool", aliases.Resolve("Tool"));
    }

    [Fact]
    public void A_bare_name_stays_when_the_same_subfolder_name_belongs_to_two_projects()
    {
        var aliases = Build([], Record(@"C:\Dev\One"), Record(@"C:\Dev\One\android"), Record(@"C:\Dev\Two"), Record(@"C:\Dev\Two\android"), Record("android"));

        Assert.Equal("android", aliases.Resolve("android"));
    }

    [Fact]
    public void A_moved_project_with_a_child_of_its_own_name_merges_both_levels()
    {
        var aliases = Build(
            [@"E:\Work\Tool", @"E:\Work\Tool\Tool"],
            Record(@"C:\a\Tool"), Record(@"C:\a\Tool\Tool"), Record(@"E:\Work\Tool"), Record(@"E:\Work\Tool\Tool"));

        Assert.Equal(@"E:\Work\Tool", aliases.Resolve(@"C:\a\Tool"));
        Assert.Equal(@"E:\Work\Tool\Tool", aliases.Resolve(@"C:\a\Tool\Tool"));
    }

    [Fact]
    public void Two_moved_projects_nested_in_each_other_under_swapped_names_both_merge()
    {
        var aliases = Build(
            [@"E:\Work\Alpha", @"E:\Work\Alpha\Beta", @"E:\Work\Beta", @"E:\Work\Beta\Alpha"],
            Record(@"C:\a\Alpha"), Record(@"C:\a\Alpha\Beta"), Record(@"C:\a\Beta"), Record(@"C:\a\Beta\Alpha"),
            Record(@"E:\Work\Alpha"), Record(@"E:\Work\Alpha\Beta"), Record(@"E:\Work\Beta"), Record(@"E:\Work\Beta\Alpha"));

        Assert.Equal(@"E:\Work\Alpha", aliases.Resolve(@"C:\a\Alpha"));
        Assert.Equal(@"E:\Work\Beta", aliases.Resolve(@"C:\a\Beta"));
        Assert.Equal(@"E:\Work\Alpha\Beta", aliases.Resolve(@"C:\a\Alpha\Beta"));
        Assert.Equal(@"E:\Work\Beta\Alpha", aliases.Resolve(@"C:\a\Beta\Alpha"));
    }

    [Fact]
    public void A_moved_project_with_bare_rows_is_one_bar_holding_every_total()
    {
        var records = new[]
        {
            Record(@"C:\Dev\Note Keeper", tokens: 10), Record(@"E:\Dev\Note Keeper", tokens: 20), Record("Note Keeper", tokens: 30),
            Record(@"E:\Dev\Other", tokens: 5), Record("Unknown", tokens: 7), Record("", tokens: 3),
        };
        var aliases = Build([@"E:\Dev\Note Keeper", @"E:\Dev\Other"], records);

        var before = StatsAggregator.TopProjectsDetailed(records, int.MaxValue, "none");
        var after = StatsAggregator.TopProjectsDetailed(aliases.Apply(records), int.MaxValue, "none");

        Assert.Equal(75, before.Sum(row => row.Total));
        Assert.Equal(75, after.Sum(row => row.Total));
        Assert.Equal(4, after.Count);
        Assert.Equal(60, after.Single(row => row.FullPath == @"E:\Dev\Note Keeper").Total);
    }

    [Fact]
    public void Former_paths_are_found_from_an_agent_work_folder_of_the_project()
    {
        var aliases = Build([@"E:\Work\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Work\AI-Usage"));

        Assert.Equal([@"C:\a\AI-Usage"], aliases.FormerPaths(@"E:\Work\AI-Usage\.tmp\wt-x"));
        Assert.Empty(aliases.FormerPaths(""));
    }

    [Fact]
    public void Apply_renames_records_and_sessions_and_keeps_the_rest()
    {
        var aliases = Build([@"E:\Work\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Work\AI-Usage"));
        var records = new[] { Record(@"C:\a\AI-Usage", tokens: 5), Record(@"Z:\other\Thing", tokens: 7) };

        var applied = aliases.Apply(records);

        Assert.Equal([@"E:\Work\AI-Usage", @"Z:\other\Thing"], applied.Select(record => record.Project));
        Assert.Equal([5L, 7L], applied.Select(record => record.TotalTokens));
        Assert.Same(records[1], applied[1]);
        Assert.Equal(@"C:\a\AI-Usage", records[0].Project);
    }

    [Fact]
    public void Apply_hands_back_the_same_list_when_nothing_is_merged()
    {
        var records = new[] { Record(@"C:\a\Tool") };
        var aliases = Build([@"C:\a\Tool"], records);

        Assert.Same(records, aliases.Apply(records));
        Assert.True(aliases.IsEmpty);
    }
}
