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
            [@"E:\Claude\ArrowMaze\review"],
            Record(@"C:\Claude\AI-Usage"), Record(@"C:\Claude\AI-Usage\review"), Record(@"C:\Claude\Prompt Saver"), Record(@"C:\Claude\Prompt Saver\review"),
            Record(@"E:\Claude\ArrowMaze"), Record(@"E:\Claude\ArrowMaze\review"));

        Assert.Equal(@"C:\Claude\AI-Usage\review", aliases.Resolve(@"C:\Claude\AI-Usage\review"));
        Assert.Equal(@"C:\Claude\Prompt Saver\review", aliases.Resolve(@"C:\Claude\Prompt Saver\review"));
        Assert.Equal(@"E:\Claude\ArrowMaze\review", aliases.Resolve(@"E:\Claude\ArrowMaze\review"));
        Assert.Empty(aliases.FormerPaths(@"E:\Claude\ArrowMaze\review"));
    }

    [Fact]
    public void A_subfolder_of_a_moved_project_follows_the_move()
    {
        var aliases = Build(
            [@"E:\Claude\Prompt Saver", @"E:\Claude\Prompt Saver\review"],
            Record(@"C:\Claude\Prompt Saver"), Record(@"E:\Claude\Prompt Saver"),
            Record(@"C:\Claude\Prompt Saver\review"), Record(@"E:\Claude\Prompt Saver\review"));

        Assert.Equal(@"E:\Claude\Prompt Saver", aliases.Resolve(@"C:\Claude\Prompt Saver"));
        Assert.Equal(@"E:\Claude\Prompt Saver\review", aliases.Resolve(@"C:\Claude\Prompt Saver\review"));
    }

    [Fact]
    public void A_subfolder_name_under_two_different_projects_stays_two()
    {
        var aliases = Build(
            [],
            Record(@"C:\Claude\Claude-Messenger"), Record(@"C:\Claude\Claude-Messenger\android", dayOffset: 1),
            Record(@"C:\Claude\IP-Scanner-App"), Record(@"C:\Claude\IP-Scanner-App\android", dayOffset: 2));

        Assert.Equal(@"C:\Claude\Claude-Messenger\android", aliases.Resolve(@"C:\Claude\Claude-Messenger\android"));
        Assert.Equal(@"C:\Claude\IP-Scanner-App\android", aliases.Resolve(@"C:\Claude\IP-Scanner-App\android"));
    }

    [Fact]
    public void A_subfolder_and_a_project_of_the_same_name_stay_apart()
    {
        var aliases = Build(
            [@"E:\Claude\Tool"],
            Record(@"C:\Claude\Other"), Record(@"C:\Claude\Other\Tool"), Record(@"E:\Claude\Tool"));

        Assert.Equal(@"C:\Claude\Other\Tool", aliases.Resolve(@"C:\Claude\Other\Tool"));
    }

    [Fact]
    public void A_bare_name_follows_the_merged_project()
    {
        var aliases = Build([@"E:\Claude\Prompt Saver"], Record(@"C:\Claude\Prompt Saver"), Record(@"E:\Claude\Prompt Saver"), Record("Prompt Saver"));

        Assert.Equal(@"E:\Claude\Prompt Saver", aliases.Resolve("Prompt Saver"));
        Assert.Equal(@"E:\Claude\Prompt Saver", aliases.Resolve("prompt saver"));
        Assert.Equal([@"C:\Claude\Prompt Saver"], aliases.FormerPaths(@"E:\Claude\Prompt Saver"));
    }

    [Fact]
    public void A_bare_session_name_resolves_to_its_project()
    {
        var session = new StatsSessionRecord(
            "codex", "", "s1", "Prompt Saver", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
            1, 0, 0, 0, "gpt-5", 0);
        var present = new HashSet<string> { @"E:\Claude\Prompt Saver" };

        var aliases = ProjectAliasResolver.Build(
            [Record(@"C:\Claude\Prompt Saver"), Record(@"E:\Claude\Prompt Saver")], [session], present.Contains);

        Assert.Equal(@"E:\Claude\Prompt Saver", aliases.Apply([session])[0].Project);
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
        var aliases = Build([], Record(@"C:\Claude\One"), Record(@"C:\Claude\One\android"), Record(@"C:\Claude\Two"), Record(@"C:\Claude\Two\android"), Record("android"));

        Assert.Equal("android", aliases.Resolve("android"));
    }

    [Fact]
    public void A_moved_project_with_bare_rows_is_one_bar_holding_every_total()
    {
        var records = new[]
        {
            Record(@"C:\Claude\Prompt Saver", tokens: 10), Record(@"E:\Claude\Prompt Saver", tokens: 20), Record("Prompt Saver", tokens: 30),
            Record(@"E:\Claude\Other", tokens: 5), Record("Unknown", tokens: 7), Record("", tokens: 3),
        };
        var aliases = Build([@"E:\Claude\Prompt Saver", @"E:\Claude\Other"], records);

        var before = StatsAggregator.TopProjectsDetailed(records, int.MaxValue, "none");
        var after = StatsAggregator.TopProjectsDetailed(aliases.Apply(records), int.MaxValue, "none");

        Assert.Equal(75, before.Sum(row => row.Total));
        Assert.Equal(75, after.Sum(row => row.Total));
        Assert.Equal(4, after.Count);
        Assert.Equal(60, after.Single(row => row.FullPath == @"E:\Claude\Prompt Saver").Total);
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
