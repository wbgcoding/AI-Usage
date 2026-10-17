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
        var aliases = Build([@"E:\Claude\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Claude\AI-Usage"));

        Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage"));
        Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(@"E:\Claude\AI-Usage"));
        Assert.Equal([@"C:\a\AI-Usage"], aliases.FormerPaths(@"E:\Claude\AI-Usage"));
    }

    [Fact]
    public void When_both_exist_the_projects_stay_two()
    {
        var aliases = Build([@"C:\a\AI-Usage", @"E:\Claude\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Claude\AI-Usage"));

        Assert.Equal(@"C:\a\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage"));
        Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(@"E:\Claude\AI-Usage"));
        Assert.Empty(aliases.FormerPaths(@"E:\Claude\AI-Usage"));
    }

    [Fact]
    public void When_none_exists_the_most_recently_used_path_wins()
    {
        var aliases = Build([], Record(@"C:\a\AI-Usage", dayOffset: 0), Record(@"E:\Claude\AI-Usage", dayOffset: 5), Record(@"D:\b\AI-Usage", dayOffset: 2));

        Assert.All([@"C:\a\AI-Usage", @"D:\b\AI-Usage", @"E:\Claude\AI-Usage"],
            path => Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(path)));
        Assert.Equal(2, aliases.FormerPaths(@"E:\Claude\AI-Usage").Count);
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
        var aliases = Build([], Record(@"C:\a\Alpha"), Record(@"C:\a\Beta"), Record("Alpha"), Record(""));

        Assert.Equal(@"C:\a\Alpha", aliases.Resolve(@"C:\a\Alpha"));
        Assert.Equal("Alpha", aliases.Resolve("Alpha"));
        Assert.Equal("", aliases.Resolve(""));
    }

    [Fact]
    public void The_same_folder_in_another_letter_case_is_one_path_not_two_that_exist()
    {
        var aliases = Build([@"E:\Claude\AI-Usage"], Record(@"e:\claude\ai-usage"), Record(@"E:\Claude\AI-Usage"), Record(@"C:\gone\AI-Usage"));

        Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(@"C:\gone\AI-Usage"), ignoreCase: true);
    }

    [Fact]
    public void An_agent_work_folder_resolves_by_its_project_root()
    {
        var aliases = Build([@"E:\Claude\AI-Usage"], Record(@"C:\a\AI-Usage\.tmp\wt-x"), Record(@"E:\Claude\AI-Usage"));

        Assert.Equal(@"E:\Claude\AI-Usage", aliases.Resolve(@"C:\a\AI-Usage\.tmp\wt-x"));
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
    public void Apply_renames_records_and_sessions_and_keeps_the_rest()
    {
        var aliases = Build([@"E:\Claude\AI-Usage"], Record(@"C:\a\AI-Usage"), Record(@"E:\Claude\AI-Usage"));
        var records = new[] { Record(@"C:\a\AI-Usage", tokens: 5), Record(@"Z:\other\Thing", tokens: 7) };

        var applied = aliases.Apply(records);

        Assert.Equal([@"E:\Claude\AI-Usage", @"Z:\other\Thing"], applied.Select(record => record.Project));
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
