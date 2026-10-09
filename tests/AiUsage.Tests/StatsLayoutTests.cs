using AiUsage.Stats;

namespace AiUsage.Tests;

public class StatsLayoutTests
{
    private const string DefaultText = "monthgrid / figures / breakdown / provider,model,effort | limits,projects / table";

    /// <summary>Compact text form: rows joined by " / ", columns by " | ", keys by ",".</summary>
    private static string Show(IEnumerable<StatsLayoutRow> rows) =>
        string.Join(" / ", rows.Select(r => r.Right.Count == 0
            ? string.Join(",", r.Left)
            : string.Join(",", r.Left) + " | " + string.Join(",", r.Right)));

    private static List<StatsLayoutRow> Rows(params (string[] Left, string[] Right)[] rows) =>
        rows.Select(r => new StatsLayoutRow { Left = [.. r.Left], Right = [.. r.Right] }).ToList();

    [Fact]
    public void Default_holds_every_key_exactly_once_and_is_a_fresh_copy()
    {
        var rows = StatsLayout.Default();

        Assert.Equal(DefaultText, Show(rows));
        var keys = rows.SelectMany(r => r.Left.Concat(r.Right)).ToList();
        Assert.Equal(StatsLayout.SectionKeys.Order(), keys.Order());
        Assert.NotSame(rows[0], StatsLayout.Default()[0]);
    }

    [Fact]
    public void The_removed_per_day_and_cache_sections_are_no_keys_and_an_old_layout_drops_them()
    {
        Assert.DoesNotContain("perday", StatsLayout.SectionKeys);
        Assert.DoesNotContain("cache", StatsLayout.SectionKeys);
        Assert.Equal(9, StatsLayout.SectionKeys.Count);

        var old = Rows((["breakdown"], []), (["perday"], []), (["cache"], []), (["table"], []));

        var result = StatsLayout.Normalize(old);

        Assert.StartsWith("breakdown / table / monthgrid / figures", Show(result));
        Assert.DoesNotContain("perday", Show(result));
        Assert.DoesNotContain("cache", Show(result));
    }

    [Fact]
    public void Normalize_null_gives_the_default()
    {
        Assert.Equal(DefaultText, Show(StatsLayout.Normalize(null)));
    }

    [Fact]
    public void Normalize_drops_unknown_and_duplicate_keys_and_appends_missing_ones()
    {
        var rows = Rows(
            (["figures", "bogus"], []),
            (["figures"], ["table"]),
            (["table"], []));

        var result = StatsLayout.Normalize(rows);

        Assert.Equal(
            "figures / table / monthgrid / breakdown / provider / model / effort / limits / projects",
            Show(result));
    }

    [Fact]
    public void Normalize_turns_a_right_only_row_into_a_single_row()
    {
        var result = StatsLayout.Normalize(Rows(([], ["breakdown"])));

        Assert.Equal("breakdown / monthgrid / figures / provider / model / effort / limits / projects / table", Show(result));
    }

    [Fact]
    public void Normalize_splits_a_one_column_row_with_several_keys_in_place()
    {
        var result = StatsLayout.Normalize(Rows((["breakdown", "table", "figures"], [])));

        Assert.StartsWith("breakdown / table / figures / monthgrid", Show(result));
    }

    [Fact]
    public void Normalize_never_changes_its_input()
    {
        var rows = Rows((["figures", "bogus"], ["figures"]));

        _ = StatsLayout.Normalize(rows);

        Assert.Equal(["figures", "bogus"], rows[0].Left);
        Assert.Equal(["figures"], rows[0].Right);
    }

    [Fact]
    public void Move_above_and_below_a_single_row_makes_a_new_single_row()
    {
        Assert.Equal(
            "monthgrid / breakdown / figures / provider,model,effort | limits,projects / table",
            Show(StatsLayout.Move(StatsLayout.Default(), "breakdown", "figures", StatsDropPosition.Above)));
        Assert.Equal(
            "monthgrid / breakdown / figures / provider,model,effort | limits,projects / table",
            Show(StatsLayout.Move(StatsLayout.Default(), "figures", "breakdown", StatsDropPosition.Below)));
    }

    [Fact]
    public void Move_above_and_below_inside_a_column_inserts_next_to_the_target()
    {
        Assert.Equal(
            "monthgrid / figures / breakdown / model,effort,provider | limits,projects / table",
            Show(StatsLayout.Move(StatsLayout.Default(), "provider", "effort", StatsDropPosition.Below)));
        Assert.Equal(
            "monthgrid / figures / breakdown / provider,effort,model | limits,projects / table",
            Show(StatsLayout.Move(StatsLayout.Default(), "effort", "model", StatsDropPosition.Above)));
        Assert.Equal(
            "monthgrid / figures / breakdown / provider,model | limits,projects,effort / table",
            Show(StatsLayout.Move(StatsLayout.Default(), "effort", "projects", StatsDropPosition.Below)));
    }

    [Fact]
    public void Move_left_or_right_of_a_single_row_makes_a_two_column_row()
    {
        var left = StatsLayout.Move(StatsLayout.Default(), "table", "breakdown", StatsDropPosition.Left);
        var right = StatsLayout.Move(StatsLayout.Default(), "table", "breakdown", StatsDropPosition.Right);

        Assert.Equal("monthgrid / figures / table | breakdown / provider,model,effort | limits,projects", Show(left));
        Assert.Equal("monthgrid / figures / breakdown | table / provider,model,effort | limits,projects", Show(right));
    }

    [Fact]
    public void Move_left_or_right_of_a_section_in_a_two_column_row_acts_as_above_or_below()
    {
        Assert.Equal(
            "monthgrid / figures / breakdown / provider,table,model,effort | limits,projects",
            Show(StatsLayout.Move(StatsLayout.Default(), "table", "model", StatsDropPosition.Above)));
        Assert.Equal(
            Show(StatsLayout.Move(StatsLayout.Default(), "table", "model", StatsDropPosition.Above)),
            Show(StatsLayout.Move(StatsLayout.Default(), "table", "model", StatsDropPosition.Left)));
        Assert.Equal(
            Show(StatsLayout.Move(StatsLayout.Default(), "table", "model", StatsDropPosition.Below)),
            Show(StatsLayout.Move(StatsLayout.Default(), "table", "model", StatsDropPosition.Right)));
    }

    [Fact]
    public void Moving_the_last_section_out_of_a_column_splits_the_other_column_in_place()
    {
        var oneLeft = StatsLayout.Move(StatsLayout.Default(), "limits", "table", StatsDropPosition.Below);
        var result = StatsLayout.Move(oneLeft, "projects", "table", StatsDropPosition.Below);

        Assert.Equal("monthgrid / figures / breakdown / provider / model / effort / table / projects / limits", Show(result));
    }

    [Fact]
    public void Move_onto_itself_or_an_unknown_key_changes_nothing()
    {
        Assert.Equal(DefaultText, Show(StatsLayout.Move(StatsLayout.Default(), "breakdown", "breakdown", StatsDropPosition.Above)));
        Assert.Equal(DefaultText, Show(StatsLayout.Move(StatsLayout.Default(), "bogus", "breakdown", StatsDropPosition.Above)));
        Assert.Equal(DefaultText, Show(StatsLayout.Move(StatsLayout.Default(), "breakdown", "bogus", StatsDropPosition.Above)));
    }

    [Fact]
    public void MoveUp_swaps_inside_a_column()
    {
        var result = StatsLayout.MoveUp(StatsLayout.Default(), "effort");

        Assert.Equal("monthgrid / figures / breakdown / provider,effort,model | limits,projects / table", Show(result));
    }

    [Fact]
    public void MoveUp_at_the_top_of_a_column_leaves_the_row_as_a_single_row_above_it()
    {
        Assert.Equal(
            "monthgrid / figures / breakdown / limits / provider,model,effort | projects / table",
            Show(StatsLayout.MoveUp(StatsLayout.Default(), "limits")));
        Assert.Equal(
            "monthgrid / figures / breakdown / provider / model,effort | limits,projects / table",
            Show(StatsLayout.MoveUp(StatsLayout.Default(), "provider")));
    }

    [Fact]
    public void MoveDown_swaps_inside_a_column_and_leaves_the_row_at_the_bottom()
    {
        Assert.Equal(
            "monthgrid / figures / breakdown / model,provider,effort | limits,projects / table",
            Show(StatsLayout.MoveDown(StatsLayout.Default(), "provider")));
        Assert.Equal(
            "monthgrid / figures / breakdown / provider,model | limits,projects / effort / table",
            Show(StatsLayout.MoveDown(StatsLayout.Default(), "effort")));
        Assert.Equal(
            "monthgrid / figures / breakdown / provider,model,effort | limits / projects / table",
            Show(StatsLayout.MoveDown(StatsLayout.Default(), "projects")));
    }

    [Fact]
    public void MoveDown_and_MoveUp_step_over_a_hidden_single_row()
    {
        string[] withoutTable = [.. StatsLayout.SectionKeys.Where(key => key != "table")];
        var rows = Rows((["figures"], []), (["table"], []), (["breakdown"], []));
        const string Tail = " / monthgrid / provider / model / effort / limits / projects";

        Assert.Equal("table / figures / breakdown" + Tail, Show(StatsLayout.MoveDown(rows, "figures")));
        Assert.Equal("breakdown / table / figures" + Tail, Show(StatsLayout.MoveDown(rows, "figures", withoutTable)));
        Assert.Equal("breakdown / table / figures" + Tail, Show(StatsLayout.MoveUp(rows, "breakdown", withoutTable)));
        // Nothing shown above it: no move.
        Assert.Equal("figures / table / breakdown" + Tail, Show(StatsLayout.MoveUp(rows, "figures", withoutTable)));
    }

    [Fact]
    public void MoveDown_inside_a_column_steps_over_a_hidden_member()
    {
        var rows = Rows((["provider", "model", "effort"], ["projects"]));
        var result = StatsLayout.MoveDown(rows, "provider", ["provider", "effort", "projects"]);

        Assert.StartsWith("effort,model,provider | projects", Show(result));
    }

    [Fact]
    public void MoveUp_and_MoveDown_swap_single_rows_and_keep_the_first_and_last_in_place()
    {
        Assert.Equal(
            "figures / monthgrid / breakdown / provider,model,effort | limits,projects / table",
            Show(StatsLayout.MoveUp(StatsLayout.Default(), "figures")));
        Assert.Equal(
            "monthgrid / figures / breakdown / table / provider,model,effort | limits,projects",
            Show(StatsLayout.MoveUp(StatsLayout.Default(), "table")));
        Assert.Equal(DefaultText, Show(StatsLayout.MoveUp(StatsLayout.Default(), "monthgrid")));
        Assert.Equal(DefaultText, Show(StatsLayout.MoveDown(StatsLayout.Default(), "table")));
        Assert.Equal(
            "figures / monthgrid / breakdown / provider,model,effort | limits,projects / table",
            Show(StatsLayout.MoveDown(StatsLayout.Default(), "monthgrid")));
    }

    [Fact]
    public void IsDefault_and_AreEqual_compare_structure()
    {
        Assert.True(StatsLayout.IsDefault(StatsLayout.Default()));
        Assert.False(StatsLayout.IsDefault(StatsLayout.MoveUp(StatsLayout.Default(), "figures")));
        Assert.True(StatsLayout.AreEqual(StatsLayout.Default(), StatsLayout.Default()));
        Assert.False(StatsLayout.AreEqual(StatsLayout.Default(), StatsLayout.MoveUp(StatsLayout.Default(), "effort")));
    }

    [Theory]
    [InlineData(100, 50, 10, 10, true, StatsDropPosition.Left)]
    [InlineData(100, 50, 90, 40, true, StatsDropPosition.Right)]
    [InlineData(100, 50, 50, 10, true, StatsDropPosition.Above)]
    [InlineData(100, 50, 50, 40, true, StatsDropPosition.Below)]
    [InlineData(100, 50, 25, 10, true, StatsDropPosition.Above)]
    [InlineData(100, 50, 75, 40, true, StatsDropPosition.Below)]
    [InlineData(100, 50, 10, 10, false, StatsDropPosition.Above)]
    [InlineData(100, 50, 90, 40, false, StatsDropPosition.Below)]
    [InlineData(100, 50, 50, 25, false, StatsDropPosition.Below)]
    public void DropZone_picks_the_side_from_the_pointer_position(double width, double height, double x, double y, bool alone, StatsDropPosition expected)
    {
        Assert.Equal(expected, AiUsage.Views.StatsDropZone.Compute(width, height, x, y, alone));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(101, 10)]
    [InlineData(50, -1)]
    [InlineData(50, 51)]
    public void DropZone_is_null_outside_the_target(double x, double y)
    {
        Assert.Null(AiUsage.Views.StatsDropZone.Compute(100, 50, x, y, true));
    }
}
