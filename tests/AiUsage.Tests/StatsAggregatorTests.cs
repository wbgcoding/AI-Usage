using System.Globalization;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Fixture-based coverage for the pure grouping and summarising logic behind the
/// statistics window's chart, table and headline number.</summary>
public class StatsAggregatorTests
{
    private static StatsRecord Record(string provider, DateOnly day, string model, string project, long total) =>
        new(provider, day, model, project, total, 0, 0, 0);

    private static StatsRecord RecordWithEffort(string provider, DateOnly day, string effort, long total) =>
        new(provider, day, "modelA", "projA", total, 0, 0, 0, Effort: effort);

    [Fact]
    public void Groups_by_day_and_stacks_by_provider()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("codex", new DateOnly(2026, 1, 1), "modelB", "projB", 50),
            Record("claude", new DateOnly(2026, 1, 2), "modelA", "projA", 30),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Day);

        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-01-01", rows[0].Label);
        Assert.Equal(150, rows[0].Total);
        Assert.Equal([100, 50], rows[0].StackedValues); // claude first, codex second
        Assert.Equal("2026-01-02", rows[1].Label);
        Assert.Equal(30, rows[1].Total);
    }

    [Fact]
    public void Groups_by_week_correctly_across_a_year_boundary()
    {
        var records = new[]
        {
            // 2025-12-29 (Monday) is ISO week 1 of 2026, not the last week of 2025.
            Record("claude", new DateOnly(2025, 12, 29), "modelA", "projA", 10),
            Record("claude", new DateOnly(2026, 1, 2), "modelA", "projA", 20),
            // 2025-12-28 (Sunday) still belongs to ISO week 52 of 2025.
            Record("codex", new DateOnly(2025, 12, 28), "modelB", "projB", 5),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Week);

        Assert.Equal(2, rows.Count);
        Assert.Equal("2025-W52", rows[0].Label);
        Assert.Equal(5, rows[0].Total);
        Assert.Equal("2026-W01", rows[1].Label);
        Assert.Equal(30, rows[1].Total); // the two 2026-W01 records combined
    }

    [Fact]
    public void Week_grouping_with_bounds_fills_empty_weeks_with_zero_rows()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 7), "modelA", "projA", 10),
            Record("codex", new DateOnly(2026, 3, 18), "modelB", "projB", 20),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Week, new DateOnly(2026, 1, 7), new DateOnly(2026, 3, 18));

        Assert.Equal(11, rows.Count);
        Assert.Equal("2026-W02", rows[0].Label);
        Assert.Equal("2026-W12", rows[10].Label);
        Assert.Equal(10, rows[0].Total);
        Assert.Equal(20, rows[10].Total);
        Assert.All(rows.Skip(1).Take(9), row =>
        {
            Assert.Equal(0, row.Total);
            Assert.Equal(StatsAggregator.StackedProviderOrder.Count, row.StackedValues.Count);
            Assert.All(row.StackedValues, value => Assert.Equal(0, value));
        });
    }

    [Fact]
    public void Week_grouping_with_bounds_crosses_the_iso_year_boundary()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2025, 12, 20), "modelA", "projA", 7),
            Record("claude", new DateOnly(2026, 1, 8), "modelA", "projA", 9),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Week, new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 8));

        Assert.Equal(["2025-W51", "2025-W52", "2026-W01", "2026-W02"], rows.Select(row => row.Label));
        Assert.Equal([7L, 0L, 0L, 9L], rows.Select(row => row.Total));
    }

    [Fact]
    public void Groups_by_model_summing_across_days_and_projects()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("claude", new DateOnly(2026, 1, 2), "modelA", "projB", 50),
            Record("codex", new DateOnly(2026, 1, 1), "modelC", "projC", 10),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Model);

        Assert.Equal(2, rows.Count);
        Assert.Equal("modelA", rows[0].Label); // largest total first
        Assert.Equal(150, rows[0].Total);
        Assert.Equal("modelC", rows[1].Label);
        Assert.Equal(10, rows[1].Total);
    }

    [Fact]
    public void Groups_by_project_summing_across_days_and_models()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 40),
            Record("claude", new DateOnly(2026, 1, 2), "modelB", "projA", 20),
            Record("codex", new DateOnly(2026, 1, 1), "modelC", "projB", 100),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Project);

        Assert.Equal(2, rows.Count);
        Assert.Equal("projB", rows[0].Label);
        Assert.Equal(100, rows[0].Total);
        Assert.Equal("projA", rows[1].Label);
        Assert.Equal(60, rows[1].Total);
    }

    [Fact]
    public void An_empty_period_summarises_to_all_zeros_without_throwing()
    {
        IReadOnlyList<StatsRecord> empty = [];

        Assert.Empty(StatsAggregator.Group(empty, StatsGrouping.Day));
        Assert.Empty(StatsAggregator.Group(empty, StatsGrouping.Model));

        var summary = StatsAggregator.Summarize(empty, empty);
        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.PreviousTotal);
        Assert.Equal(0, summary.InputTokens);
    }

    [Fact]
    public void Summarize_reports_the_current_and_previous_period_totals_separately()
    {
        var current = new[] { new StatsRecord("claude", new DateOnly(2026, 1, 10), "modelA", "projA", 100, 50, 10, 5) };
        var previous = new[] { new StatsRecord("claude", new DateOnly(2026, 1, 3), "modelA", "projA", 40, 20, 0, 0) };

        var summary = StatsAggregator.Summarize(current, previous);

        Assert.Equal(165, summary.Total); // 100 + 50 + 10 + 5
        Assert.Equal(60, summary.PreviousTotal);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(50, summary.OutputTokens);
        Assert.Equal(15, summary.CacheTokens); // 10 + 5
    }

    // the figures bar's four cards, over a three-day fixture - one day per record, one of
    // them clearly the busiest.
    [Fact]
    public void ComputeHeadlineFigures_covers_all_four_figures_over_a_three_day_fixture()
    {
        var current = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("claude", new DateOnly(2026, 1, 2), "modelA", "projA", 300), // the busiest day
            Record("codex", new DateOnly(2026, 1, 3), "modelB", "projB", 200),
        };
        var previous = new[] { Record("claude", new DateOnly(2025, 12, 29), "modelA", "projA", 400) };

        var figures = StatsAggregator.ComputeHeadlineFigures(current, previous, periodDayCount: 3);

        Assert.Equal(600, figures.Total); // 100 + 300 + 200
        Assert.Equal(200, figures.PerDayAverage); // 600 / 3
        Assert.Equal(new DateOnly(2026, 1, 2), figures.BusiestDay);
        Assert.Equal(300, figures.BusiestDayTotal);
        Assert.True(figures.HasPreviousPeriod);
        Assert.Equal(50, figures.ChangePercent); // (600 - 400) / 400 * 100
    }

    // The one caveat: an empty preceding range must read as "no figure to show", not "0%" - the
    // flag the view keys its em-dash display off of.
    [Fact]
    public void ComputeHeadlineFigures_reports_no_previous_period_for_an_empty_preceding_range()
    {
        var current = new[] { Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100) };
        IReadOnlyList<StatsRecord> previous = [];

        var figures = StatsAggregator.ComputeHeadlineFigures(current, previous, periodDayCount: 1);

        Assert.False(figures.HasPreviousPeriod);
        Assert.Equal(0, figures.ChangePercent);
    }

    [Fact]
    public void ComputeHeadlineFigures_reports_no_busiest_day_for_an_empty_period_without_throwing()
    {
        IReadOnlyList<StatsRecord> empty = [];

        var figures = StatsAggregator.ComputeHeadlineFigures(empty, empty, periodDayCount: 7);

        Assert.Null(figures.BusiestDay);
        Assert.Equal(0, figures.Total);
        Assert.Equal(0, figures.PerDayAverage);
        Assert.False(figures.HasPreviousPeriod);
    }

    // The fifth card's own count: only days that actually saw tokens, not every calendar day in
    // the period - a 30-day period with usage on just 3 of them still reports 3, not 30.
    [Fact]
    public void ComputeHeadlineFigures_counts_only_the_days_that_actually_saw_tokens()
    {
        var current = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("claude", new DateOnly(2026, 1, 5), "modelA", "projA", 50),
            Record("codex", new DateOnly(2026, 1, 10), "modelB", "projB", 20),
        };

        var figures = StatsAggregator.ComputeHeadlineFigures(current, [], periodDayCount: 30);

        Assert.Equal(3, figures.ActiveDayCount);
    }

    [Fact]
    public void ComputeHeadlineFigures_reports_zero_active_days_for_an_empty_period()
    {
        IReadOnlyList<StatsRecord> empty = [];

        var figures = StatsAggregator.ComputeHeadlineFigures(empty, empty, periodDayCount: 30);

        Assert.Equal(0, figures.ActiveDayCount);
    }

    // The figures bar's five mini charts (847) all draw from this one shared series - its length
    // must always match the period's own calendar-day count, including the leading and trailing
    // days that saw no usage at all, so every card's chart lines up with the same days.
    [Fact]
    public void DailyTotalsSeries_is_exactly_periodDayCount_long_with_zeros_for_days_without_usage()
    {
        var current = new[]
        {
            Record("claude", new DateOnly(2026, 1, 2), "modelA", "projA", 100),
            Record("codex", new DateOnly(2026, 1, 4), "modelB", "projB", 50),
        };

        var series = StatsAggregator.DailyTotalsSeries(current, from: new DateOnly(2026, 1, 1), periodDayCount: 5);

        Assert.Equal([0, 100, 0, 50, 0], series);
    }

    [Fact]
    public void DailyTotalsSeries_sums_every_record_on_the_same_day_into_one_entry()
    {
        var current = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("codex", new DateOnly(2026, 1, 1), "modelB", "projB", 50),
        };

        var series = StatsAggregator.DailyTotalsSeries(current, from: new DateOnly(2026, 1, 1), periodDayCount: 1);

        Assert.Equal([150], series);
    }

    [Fact]
    public void DailyTotalsSeries_floors_a_zero_or_negative_periodDayCount_to_one_entry()
    {
        IReadOnlyList<StatsRecord> empty = [];

        var series = StatsAggregator.DailyTotalsSeries(empty, from: new DateOnly(2026, 1, 1), periodDayCount: 0);

        Assert.Equal([0L], series);
    }

    [Fact]
    public void BusiestIndex_finds_the_largest_entry()
    {
        Assert.Equal(2, StatsAggregator.BusiestIndex([10, 20, 90, 5]));
    }

    [Fact]
    public void BusiestIndex_keeps_the_first_entry_on_a_tie()
    {
        Assert.Equal(0, StatsAggregator.BusiestIndex([50, 50, 10]));
    }

    [Fact]
    public void BusiestIndex_is_null_for_an_empty_series()
    {
        Assert.Null(StatsAggregator.BusiestIndex([]));
    }

    // a day inside the requested range with no records at all still gets its own row, all
    // zeros - the row that makes the "Per day" chart draw an empty slot instead of skipping straight
    // to the next day that does have data.
    [Fact]
    public void Group_by_day_fills_a_gap_day_with_zeros_when_a_range_is_given()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            // 2026-01-02 has no records at all.
            Record("codex", new DateOnly(2026, 1, 3), "modelB", "projB", 40),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Day, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3));

        Assert.Equal(3, rows.Count);
        Assert.Equal("2026-01-01", rows[0].Label);
        Assert.Equal(100, rows[0].Total);
        Assert.Equal("2026-01-02", rows[1].Label);
        Assert.Equal(0, rows[1].Total);
        Assert.Equal([0, 0], rows[1].StackedValues);
        Assert.Equal("2026-01-03", rows[2].Label);
        Assert.Equal(40, rows[2].Total);
    }

    [Fact]
    public void Group_by_day_without_a_range_keeps_the_old_records_only_behaviour()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100),
            Record("claude", new DateOnly(2026, 1, 3), "modelA", "projA", 40),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Day);

        Assert.Equal(2, rows.Count); // no filled-in 2026-01-02
    }

    // Only providers with locally countable token data are listed, sorted by share descending.
    [Fact]
    public void ShareByProvider_lists_only_countable_providers_sorted_by_share()
    {
        var records = new[]
        {
            Record("codex", new DateOnly(2026, 1, 1), "modelA", "projA", 25),
            Record("claude", new DateOnly(2026, 1, 1), "modelB", "projB", 75),
        };

        var slices = StatsAggregator.ShareByProvider(records);

        Assert.Equal(["claude", "codex"], slices.Select(slice => slice.Label).ToArray());
        Assert.Equal(75, slices[0].Total);
        Assert.Equal(75.0, slices[0].Percent);
        Assert.Equal(25, slices[1].Total);
        Assert.Equal(25.0, slices[1].Percent);
    }

    // The acceptance criterion here: seven models collapse to six plus one pooled entry.
    [Fact]
    public void ShareByModel_pools_everything_past_the_top_six_into_one_other_entry()
    {
        var records = Enumerable.Range(1, 7)
            .Select(i => Record("claude", new DateOnly(2026, 1, 1), $"model{i}", "proj", i * 10))
            .ToArray();

        var slices = StatsAggregator.ShareByModel(records, topCount: 6, otherLabel: "Other");

        Assert.Equal(7, slices.Count); // six named models plus one pooled "Other"
        Assert.Equal("model7", slices[0].Label); // largest (70) first
        Assert.Equal("Other", slices[^1].Label); // the smallest (model1, 10) is the only one pooled
        Assert.Equal(10, slices[^1].Total);
        Assert.Equal(100.0, slices.Sum(slice => slice.Percent), 1); // every slice's percent sums to the whole
    }

    [Fact]
    public void ShareByModel_omits_the_other_entry_when_nothing_needs_pooling()
    {
        var records = new[] { Record("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 10) };

        var slices = StatsAggregator.ShareByModel(records, topCount: 6, otherLabel: "Other");

        Assert.Single(slices);
        Assert.Equal("modelA", slices[0].Label);
    }

    [Fact]
    public void ShareByModel_merges_two_ids_of_one_model_into_one_slice()
    {
        var day = new DateOnly(2026, 1, 1);
        var records = new[]
        {
            Record("claude", day, "claude-opus-4-8", "proj", 70),
            Record("claude", day, "claude-opus-4-8-20260101", "proj", 30),
            Record("codex", day, "gpt-5-codex", "proj", 20),
        };

        var slices = StatsAggregator.ShareByModel(records, topCount: 6, otherLabel: "Other");

        Assert.Equal(2, slices.Count);
        Assert.Equal("Opus 4.8", slices[0].Label);
        Assert.Equal(100, slices[0].Total);
        Assert.Equal("claude", slices[0].ProviderId);
        Assert.Equal("GPT-5 Codex", slices[1].Label);
    }

    [Fact]
    public void Group_by_model_merges_two_ids_of_one_model_into_one_row()
    {
        var day = new DateOnly(2026, 1, 1);
        var records = new[]
        {
            Record("claude", day, "claude-opus-4-8", "proj", 70),
            Record("claude", day, "claude-opus-4-8-20260101", "proj", 30),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Model);

        var row = Assert.Single(rows);
        Assert.Equal("Opus 4.8", row.Label);
        Assert.Equal(100, row.Total);
    }

    [Fact]
    public void DayDetail_merges_two_ids_of_one_model_into_one_slice()
    {
        var day = new DateOnly(2026, 1, 1);
        var records = new[]
        {
            Record("claude", day, "claude-opus-4-8", "proj", 70),
            Record("claude", day, "claude-opus-4-8-20260101", "proj", 30),
        };

        var detail = StatsAggregator.DayDetail(records, day, culture: new CultureInfo("de-DE"));

        var slice = Assert.Single(detail.ByModel);
        Assert.Equal("Opus 4.8", slice.Label);
        Assert.Equal(100, slice.Total);
        Assert.Equal("claude", slice.ProviderId);
    }

    [Fact]
    public void ShareByModel_names_each_slices_own_provider_and_the_bigger_one_for_a_shared_model_name()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 10),
            // "modelB" is used by both providers here - codex contributed the larger sum (30 vs 20),
            // so the slice names codex, not claude.
            Record("claude", new DateOnly(2026, 1, 1), "modelB", "proj", 20),
            Record("codex", new DateOnly(2026, 1, 1), "modelB", "proj", 30),
        };

        var slices = StatsAggregator.ShareByModel(records, topCount: 6, otherLabel: "Other");

        Assert.Equal("modelB", slices[0].Label); // largest total (50) first
        Assert.Equal("codex", slices[0].ProviderId);
        Assert.Equal("modelA", slices[1].Label);
        Assert.Equal("claude", slices[1].ProviderId);
    }

    [Fact]
    public void CacheShare_sums_the_four_token_kinds_separately_across_every_record()
    {
        var records = new[]
        {
            new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 100, 50, 10, 5),
            new StatsRecord("codex", new DateOnly(2026, 1, 2), "modelB", "projB", 40, 20, 0, 15),
        };

        var cacheShare = StatsAggregator.CacheShare(records);

        Assert.Equal(140, cacheShare.NewInput); // 100 + 40
        Assert.Equal(10, cacheShare.CacheWrite);
        Assert.Equal(20, cacheShare.CacheRead); // 5 + 15
        Assert.Equal(70, cacheShare.Output); // 50 + 20
        Assert.Equal([140L, 10L, 20L, 70L], cacheShare.AsValues);
    }

    // The acceptance criterion here: eleven projects produce ten rows in descending order, and
    // nameless records land in the pooled entry.
    [Fact]
    public void TopProjects_caps_at_ten_rows_in_descending_order_and_pools_nameless_records()
    {
        var records = Enumerable.Range(1, 11)
            .Select(i => Record("claude", new DateOnly(2026, 1, 1), "modelA", $"proj{i}", i * 10))
            .Append(Record("codex", new DateOnly(2026, 1, 2), "modelB", "", 5)) // Codex rollout, no project
            .ToArray();

        var rows = StatsAggregator.TopProjects(records, limit: 10, noProjectLabel: "No project");

        Assert.Equal(10, rows.Count);
        Assert.Equal("proj11", rows[0].Label); // largest (110) first
        Assert.Equal(110, rows[0].Total);
        Assert.True(rows.Zip(rows.Skip(1), (a, b) => a.Total >= b.Total).All(inOrder => inOrder)); // strictly descending
        Assert.DoesNotContain(rows, row => row.Label == "proj1"); // smallest (10) - the eleventh row, capped out
        Assert.DoesNotContain(rows, row => row.Label == "");
    }

    [Fact]
    public void TopProjects_pools_every_nameless_record_under_one_entry()
    {
        var records = new[]
        {
            Record("codex", new DateOnly(2026, 1, 1), "modelA", "", 10),
            Record("codex", new DateOnly(2026, 1, 2), "modelB", "", 20),
            Record("claude", new DateOnly(2026, 1, 3), "modelC", "realProj", 5),
        };

        var rows = StatsAggregator.TopProjects(records, limit: 10, noProjectLabel: "No project");

        Assert.Equal(2, rows.Count);
        Assert.Equal("No project", rows[0].Label);
        Assert.Equal(30, rows[0].Total); // 10 + 20 pooled together
        Assert.Equal("realProj", rows[1].Label);
    }

    [Theory]
    [InlineData(@"C:\code\projects\ai-usage", "ai-usage")]
    [InlineData("/srv/code/projects/ai-usage/", "ai-usage")] // trailing separator trimmed first
    [InlineData("No project", "No project")] // not a path at all: passes through unchanged
    public void ShortenProjectLabel_keeps_only_the_last_folder_segment(string fullPath, string expected)
    {
        Assert.Equal(expected, StatsAggregator.ShortenProjectLabel(fullPath));
    }

    [Fact]
    public void TopProjects_pools_agent_work_folders_under_their_project()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "m", @"C:\code\ai-usage\.claude\worktrees\agent-a28e4fd1", 10),
            Record("claude", new DateOnly(2026, 1, 2), "m", @"C:\code\ai-usage\.claude\worktrees\agent-afb9741b\src", 20),
            Record("claude", new DateOnly(2026, 1, 3), "m", "/c/code/other/.claude/worktrees/agent-1", 5),
            Record("claude", new DateOnly(2026, 1, 4), "m", @"C:\code\ai-usage", 40),
            Record("claude", new DateOnly(2026, 1, 5), "m", @"C:\code\ai-usage\.tmp\agents", 7),
            Record("claude", new DateOnly(2026, 1, 6), "m", @"C:\code\ai-usage\.TMP\lanes\a", 3),
            Record("claude", new DateOnly(2026, 1, 7), "m", @"C:\code\ai-usage\.worktrees\lane-1", 2),
        };

        var rows = StatsAggregator.TopProjects(records, limit: 10, noProjectLabel: "No project")
            .Select(row => (row.Label, row.Total))
            .ToArray();

        Assert.Equal([(@"C:\code\ai-usage", 82L), ("/c/code/other", 5L)], rows);
    }

    [Theory]
    [InlineData(@"C:\code\X\.tmp\agents", @"C:\code\X")]
    [InlineData(@"C:\code\X\.claude\worktrees\abc", @"C:\code\X")]
    [InlineData(@"C:\code\X\.worktrees", @"C:\code\X")]
    [InlineData("/c/code/X/.tmp/lanes/a", "/c/code/X")]
    [InlineData(@"C:\code\X\", @"C:\code\X")]
    [InlineData(@"C:\code\X\.tmpfiles", @"C:\code\X\.tmpfiles")]
    [InlineData("Sample Tool", "Sample Tool")]
    public void ProjectKey_cuts_agent_work_folders_and_trailing_separators(string project, string expected)
    {
        Assert.Equal(expected, StatsAggregator.ProjectKey(project));
    }

    [Fact]
    public void TopProjects_merges_a_bare_codex_name_with_the_claude_path_after_resolving()
    {
        var records = StatsAggregator.ResolveBareProjectNames(
        [
            Record("codex", new DateOnly(2026, 1, 1), "m", "Sample Tool", 10),
            Record("claude", new DateOnly(2026, 1, 2), "m", @"C:\code\Sample Tool", 20),
            Record("claude", new DateOnly(2026, 1, 3), "m", @"C:\code\Sample Tool\.tmp\agents", 5),
        ]);

        var rows = StatsAggregator.TopProjects(records, limit: 10, noProjectLabel: "No project");

        var row = Assert.Single(rows);
        Assert.Equal((@"C:\code\Sample Tool", 35L), (row.Label, row.Total));
    }

    [Fact]
    public void ResolveBareProjectNames_leaves_unknown_and_ambiguous_names_alone()
    {
        var records = new[]
        {
            Record("codex", new DateOnly(2026, 1, 1), "m", "Unknown", 1),
            Record("codex", new DateOnly(2026, 1, 1), "m", "Twin", 2),
            Record("claude", new DateOnly(2026, 1, 1), "m", @"C:\a\Twin", 3),
            Record("claude", new DateOnly(2026, 1, 1), "m", @"D:\b\Twin", 4),
            Record("codex", new DateOnly(2026, 1, 1), "m", "", 5),
        };

        var resolved = StatsAggregator.ResolveBareProjectNames(records);

        Assert.Equal(records.Select(record => record.Project), resolved.Select(record => record.Project));
    }

    [Fact]
    public void Project_grouping_ignores_letter_case()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "m", @"C:\code\Sample Tool", 10),
            Record("codex", new DateOnly(2026, 1, 2), "m", @"c:\code\sample tool", 20),
        };

        var grouped = StatsAggregator.Group(records, StatsGrouping.Project);
        var top = StatsAggregator.TopProjects(records, limit: 10, noProjectLabel: "No project");

        Assert.Equal(30L, Assert.Single(grouped).Total);
        Assert.Equal(30L, Assert.Single(top).Total);
    }

    [Fact]
    public void ResolveBareProjectNames_matches_the_folder_name_ignoring_case()
    {
        var records = StatsAggregator.ResolveBareProjectNames(
        [
            Record("codex", new DateOnly(2026, 1, 1), "m", "sample tool", 10),
            Record("claude", new DateOnly(2026, 1, 2), "m", @"C:\code\Sample Tool", 20),
        ]);

        Assert.Equal(@"C:\code\Sample Tool", records[0].Project);
    }

    [Fact]
    public void MiddleEllipsis_leaves_a_label_that_already_fits_alone()
    {
        Assert.Equal("short-name", StatsAggregator.MiddleEllipsis("short-name", 20));
    }

    [Fact]
    public void MiddleEllipsis_cuts_out_the_middle_and_keeps_both_ends()
    {
        var shortened = StatsAggregator.MiddleEllipsis("a-very-long-project-folder-name", 16);

        Assert.Equal(16, shortened.Length);
        Assert.StartsWith("a-ve", shortened);
        Assert.EndsWith("-name", shortened);
        Assert.Contains("...", shortened);
    }

    // The acceptance criterion here: the row a caller sees carries both the shortened label the bar
    // draws and the full path plus first/last activity day the tooltip needs.
    [Fact]
    public void TopProjectsDetailed_carries_the_full_path_and_activity_range_alongside_the_short_label()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", @"C:\code\ai-usage", 70),
            Record("claude", new DateOnly(2026, 1, 5), "modelA", @"C:\code\ai-usage", 30),
            Record("codex", new DateOnly(2026, 1, 3), "modelB", @"C:\code\other", 20),
        };

        var rows = StatsAggregator.TopProjectsDetailed(records, limit: 10, noProjectLabel: "No project");

        Assert.Equal("ai-usage", rows[0].ShortLabel);
        Assert.Equal(@"C:\code\ai-usage", rows[0].FullPath);
        Assert.Equal(100, rows[0].Total);
        Assert.Equal(new DateOnly(2026, 1, 1), rows[0].FirstActivity);
        Assert.Equal(new DateOnly(2026, 1, 5), rows[0].LastActivity);
        Assert.Equal(100.0 * 100 / 120, rows[0].Percent);
    }

    // The acceptance criterion here: a record on a known date lands in the expected weekday
    // bucket, proven once under German and once under English so the labels are shown to come from
    // the culture rather than from a literal list.
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void GroupByWeekday_uses_the_cultures_own_day_names_and_starts_on_monday(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        var day = new DateOnly(2026, 3, 16); // a Monday
        var records = new[] { Record("claude", day, "modelA", "projA", 100) };

        var rows = StatsAggregator.GroupByWeekday(records, culture);

        Assert.Equal(7, rows.Count);
        // Monday first in every culture, matching the week used by the day grid and the week totals.
        Assert.Equal(culture.DateTimeFormat.AbbreviatedDayNames[(int)DayOfWeek.Monday], rows[0].Label);
        Assert.Equal(culture.DateTimeFormat.AbbreviatedDayNames[(int)DayOfWeek.Sunday], rows[6].Label);

        // The one record with data lands under its own day's culture-provided label, everything
        // else stays at zero.
        var mondayLabel = culture.DateTimeFormat.AbbreviatedDayNames[(int)DayOfWeek.Monday];
        var matching = Assert.Single(rows, row => row.Total == 100);
        Assert.Equal(mondayLabel, matching.Label);
        Assert.Equal(6, rows.Count(row => row.Total == 0));
    }

    // 24 fixed rows, the one record's own hour is the only one carrying a total, labelled in
    // the caller's own culture rather than a fixed "05:00" style.
    [Fact]
    public void GroupByHour_puts_each_record_under_its_own_hour()
    {
        var culture = new CultureInfo("en-US");
        var records = new[] { Record("claude", new DateOnly(2026, 3, 16), "modelA", "projA", 100) with { Hour = 5 } };

        var rows = StatsAggregator.GroupByHour(records, culture);

        Assert.Equal(24, rows.Count);
        var matching = Assert.Single(rows, row => row.Total == 100);
        Assert.Equal(new DateTime(2000, 1, 1, 5, 0, 0).ToString("t", culture), matching.Label);
        Assert.Equal(23, rows.Count(row => row.Total == 0));
    }

    [Fact]
    public void ShortenTokenCount_uses_the_callers_own_magnitude_words()
    {
        Assert.Equal(999L.ToString("N0", CultureInfo.CurrentCulture), StatsAggregator.ShortenTokenCount(999, "Tsd", "Mio", "Mrd"));
        Assert.Equal($"{12.0.ToString("0.0", CultureInfo.CurrentCulture)} Tsd", StatsAggregator.ShortenTokenCount(12_000, "Tsd", "Mio", "Mrd"));
        Assert.Equal($"{1.5.ToString("0.00", CultureInfo.CurrentCulture)} Mio", StatsAggregator.ShortenTokenCount(1_500_000, "Tsd", "Mio", "Mrd"));
        Assert.Equal($"{2.0.ToString("0.00", CultureInfo.CurrentCulture)} Mrd", StatsAggregator.ShortenTokenCount(2_000_000_000, "Tsd", "Mio", "Mrd"));
    }

    [Theory]
    [InlineData(1_000_000_000L, "1.00 B")]
    [InlineData(33_500_000L, "33.5 M")]
    [InlineData(22_000_000L, "22.0 M")]
    [InlineData(845_000L, "845 K")]
    [InlineData(1_823_457L, "1.82 M")]
    [InlineData(999L, "999")]
    [InlineData(1_000L, "1.00 K")]
    [InlineData(999_950L, "1.00 M")]
    [InlineData(9_996_000L, "10.0 M")]
    [InlineData(945_804_386L, "946 M")]
    public void ShortenTokenCountKeepsThreeSignificantDigits(long value, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(expected, StatsAggregator.ShortenTokenCount(value, "K", "M", "B"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ShortenTokenCountAxis_keeps_one_decimal_at_most()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("20 M", StatsAggregator.ShortenTokenCountAxis(20_000_000, "M", "B"));
            Assert.Equal("1.5 B", StatsAggregator.ShortenTokenCountAxis(1_500_000_000, "M", "B"));
            Assert.Equal("500,000", StatsAggregator.ShortenTokenCountAxis(500_000, "M", "B"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ShortenTokenCountCompact_abbreviates_thousands_too_unlike_ShortenTokenCount()
    {
        Assert.Equal(999L.ToString("N0", CultureInfo.CurrentCulture), StatsAggregator.ShortenTokenCountCompact(999, " Tsd", " Mio", " Mrd"));
        Assert.Equal($"{12.0.ToString("0.#", CultureInfo.CurrentCulture)} Tsd", StatsAggregator.ShortenTokenCountCompact(12_000, " Tsd", " Mio", " Mrd"));
        Assert.Equal($"{380.0.ToString("0.#", CultureInfo.CurrentCulture)} Mio", StatsAggregator.ShortenTokenCountCompact(380_000_000, " Tsd", " Mio", " Mrd"));
        Assert.Equal($"{4.2.ToString("0.#", CultureInfo.CurrentCulture)} Mrd", StatsAggregator.ShortenTokenCountCompact(4_200_000_000, " Tsd", " Mio", " Mrd"));
    }

    [Fact]
    public void ShortenTokenCountCompact_never_inserts_its_own_separator()
    {
        // The English suffixes carry no leading space of their own - "12K", never "12 K".
        Assert.Equal("12K", StatsAggregator.ShortenTokenCountCompact(12_000, "K", "M", "B"));
    }

    // the month grid's day-detail panel: two providers, three models, two projects, all four on
    // the same day - every list comes back sorted by total, descending, and the hour list always
    // holds all 24 hours once the day has any record at all.
    [Fact]
    public void DayDetail_breaks_a_day_down_by_provider_model_project_and_hour()
    {
        var day = new DateOnly(2026, 3, 10);
        var otherDay = day.AddDays(1);
        var records = new[]
        {
            Record("claude", day, "modelA", "projA", 100) with { Hour = 5 },
            Record("claude", day, "modelB", "projA", 50) with { Hour = 6 },
            Record("codex", day, "modelC", "projB", 30) with { Hour = 5 },
            Record("claude", otherDay, "modelA", "projA", 999) with { Hour = 5 }, // a different day never leaks in
        };

        var detail = StatsAggregator.DayDetail(records, day, culture: new CultureInfo("de-DE"));

        Assert.Equal(2, detail.ByProvider.Count);
        Assert.Equal("claude", detail.ByProvider[0].Label);
        Assert.Equal(150, detail.ByProvider[0].Total);
        Assert.Equal("codex", detail.ByProvider[1].Label);
        Assert.Equal(30, detail.ByProvider[1].Total);

        Assert.Equal(3, detail.ByModel.Count);
        Assert.Equal(["modelA", "modelB", "modelC"], detail.ByModel.Select(slice => slice.Label));
        Assert.Equal([100L, 50L, 30L], detail.ByModel.Select(slice => slice.Total));

        Assert.Equal(2, detail.ByProject.Count);
        Assert.Equal("projA", detail.ByProject[0].Label);
        Assert.Equal(150, detail.ByProject[0].Total);
        Assert.Equal("projB", detail.ByProject[1].Label);
        Assert.Equal(30, detail.ByProject[1].Total);

        Assert.Equal(24, detail.ByHour.Count);
        Assert.Equal("05:00", detail.ByHour[0].Label); // the busiest hour (100 + 30) first
        Assert.Equal(130, detail.ByHour[0].Total);
        Assert.Equal("06:00", detail.ByHour[1].Label);
        Assert.Equal(50, detail.ByHour[1].Total);
        Assert.Equal(22, detail.ByHour.Count(slice => slice.Total == 0));
    }

    [Fact]
    public void DayDetail_labels_hours_with_the_culture_short_time_pattern()
    {
        var day = new DateOnly(2026, 3, 16);
        var records = new[] { Record("claude", day, "modelA", "projA", 100) with { Hour = 13 } };

        var english = StatsAggregator.DayDetail(records, day, culture: new CultureInfo("en-US"));
        var german = StatsAggregator.DayDetail(records, day, culture: new CultureInfo("de-DE"));

        Assert.Equal("1:00 PM", english.ByHour[0].Label);
        Assert.Equal(13, english.ByHour[0].Hour);
        Assert.Equal("13:00", german.ByHour[0].Label);
    }

    [Fact]
    public void DayDetail_returns_four_empty_lists_for_a_day_with_no_records()
    {
        var records = new[] { Record("claude", new DateOnly(2026, 3, 10), "modelA", "projA", 10) };

        var detail = StatsAggregator.DayDetail(records, new DateOnly(2026, 3, 11));

        Assert.Empty(detail.ByProvider);
        Assert.Empty(detail.ByModel);
        Assert.Empty(detail.ByProject);
        Assert.Empty(detail.ByHour);
    }

    [Fact]
    public void Group_by_project_pools_records_without_a_project_under_the_given_label()
    {
        var records = new[]
        {
            Record("claude", new DateOnly(2026, 1, 1), "modelA", "C:\\Work\\ProjA", 100),
            Record("codex", new DateOnly(2026, 1, 1), "modelB", "", 40),
            Record("codex", new DateOnly(2026, 1, 2), "modelB", "", 10),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Project, noProjectLabel: "No project");

        Assert.Equal(["C:\\Work\\ProjA", "No project"], rows.Select(row => row.Label));
        Assert.Equal(50, rows[1].Total);
    }

    [Fact]
    public void DayDetail_pools_records_without_a_project_under_the_given_label()
    {
        var day = new DateOnly(2026, 3, 10);
        var records = new[]
        {
            Record("claude", day, "modelA", "projA", 100),
            Record("codex", day, "modelB", "", 30),
        };

        var detail = StatsAggregator.DayDetail(records, day, "No project");

        Assert.Equal(["projA", "No project"], detail.ByProject.Select(slice => slice.Label));
    }

    [Fact]
    public void ComputeHeadlineFigures_shows_no_change_when_less_than_a_quarter_of_the_previous_days_saw_tokens()
    {
        var current = new[] { Record("claude", new DateOnly(2026, 1, 20), "modelA", "projA", 100) };
        var previous = new[] { Record("claude", new DateOnly(2026, 1, 1), "modelA", "projA", 50) };

        // 30 previous days need at least 8 active ones; one is not enough.
        Assert.False(StatsAggregator.ComputeHeadlineFigures(current, previous, 30, previousPeriodDays: 30).HasPreviousPeriod);
        // 7 previous days need 2.
        Assert.False(StatsAggregator.ComputeHeadlineFigures(current, previous, 7, previousPeriodDays: 7).HasPreviousPeriod);
        // 4 previous days need 1.
        Assert.True(StatsAggregator.ComputeHeadlineFigures(current, previous, 4, previousPeriodDays: 4).HasPreviousPeriod);
    }

    [Fact]
    public void ComputeHeadlineFigures_shows_the_change_once_enough_previous_days_saw_tokens()
    {
        var current = new[] { Record("claude", new DateOnly(2026, 1, 20), "modelA", "projA", 100) };
        var previous = Enumerable.Range(0, 8).Select(i => Record("claude", new DateOnly(2026, 1, 1).AddDays(i), "modelA", "projA", 10)).ToArray();

        var figures = StatsAggregator.ComputeHeadlineFigures(current, previous, 30, previousPeriodDays: 30);

        Assert.True(figures.HasPreviousPeriod);
        Assert.Equal(25, figures.ChangePercent, 6);
    }

    // Grouping and sharing by effort level, the fifth axis alongside day/week/model/project.
    [Fact]
    public void Groups_by_effort_with_the_raw_level_as_the_label()
    {
        var records = new[]
        {
            RecordWithEffort("claude", new DateOnly(2026, 1, 1), "medium", 100),
            RecordWithEffort("codex", new DateOnly(2026, 1, 1), "high", 50),
            RecordWithEffort("claude", new DateOnly(2026, 1, 2), "medium", 30),
        };

        var rows = StatsAggregator.Group(records, StatsGrouping.Effort);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Label == "medium" && row.Total == 130);
        Assert.Contains(rows, row => row.Label == "high" && row.Total == 50);
    }

    [Fact]
    public void Groups_by_effort_keeps_an_unknown_record_under_the_empty_label()
    {
        var records = new[] { RecordWithEffort("claude", new DateOnly(2026, 1, 1), "", 10) };

        var rows = StatsAggregator.Group(records, StatsGrouping.Effort);

        Assert.Equal("", Assert.Single(rows).Label); // the view model, not the pure aggregator, substitutes the localized "unknown" text
    }

    [Fact]
    public void ShareByEffort_sorts_by_share_descending()
    {
        var records = new[]
        {
            RecordWithEffort("claude", new DateOnly(2026, 1, 1), "low", 10),
            RecordWithEffort("claude", new DateOnly(2026, 1, 1), "high", 90),
        };

        var slices = StatsAggregator.ShareByEffort(records);

        Assert.Equal(2, slices.Count);
        Assert.Equal("high", slices[0].Label);
        Assert.Equal(90.0, slices[0].Percent);
        Assert.Equal("low", slices[1].Label);
        Assert.Equal(10.0, slices[1].Percent);
    }
}
