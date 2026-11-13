using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

public class StatsRowViewModelTests
{
    [Fact]
    public void The_optional_values_default_to_a_plain_row_with_no_filter_tooltip_or_cost()
    {
        var row = new StatsRowViewModel("Monday", 1200, "1.2K");

        Assert.Equal("Monday", row.Label);
        Assert.Equal(1200, row.TotalTokens);
        Assert.Equal("1.2K", row.TotalText);
        Assert.Equal("", row.ToolTip);
        Assert.Equal(StatsFilterKind.None, row.FilterKind);
        Assert.Equal("", row.FilterKey);
        Assert.Equal("", row.CostUsd);
    }

    [Fact]
    public void A_project_row_carries_its_full_path_and_filter_back()
    {
        var row = new StatsRowViewModel(
            "app", 5_000_000_000, "5B", @"C:\work\deep\app", StatsFilterKind.Project, @"C:\work\deep\app", "12.50");

        Assert.Equal(@"C:\work\deep\app", row.ToolTip);
        Assert.Equal(StatsFilterKind.Project, row.FilterKind);
        Assert.Equal(@"C:\work\deep\app", row.FilterKey);
        Assert.Equal("12.50", row.CostUsd);
        Assert.Equal(5_000_000_000, row.TotalTokens);
    }

    [Fact]
    public void Rows_with_the_same_values_are_equal_and_a_changed_value_makes_them_differ()
    {
        var a = new StatsRowViewModel("m", 1, "1", FilterKind: StatsFilterKind.Model, FilterKey: "m");
        var b = new StatsRowViewModel("m", 1, "1", FilterKind: StatsFilterKind.Model, FilterKey: "m");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { FilterKind = StatsFilterKind.Project });
        Assert.NotEqual(a, a with { CostUsd = "0.01" });
    }

    [Fact]
    public void The_filter_kinds_keep_none_as_the_zero_value()
    {
        Assert.Equal(0, (int)StatsFilterKind.None);
        Assert.Equal(default, StatsFilterKind.None);
        Assert.Equal(3, Enum.GetValues<StatsFilterKind>().Length);
    }

    [Fact]
    public void A_zero_or_negative_token_count_is_kept_as_given()
    {
        Assert.Equal(0, new StatsRowViewModel("x", 0, "0").TotalTokens);
        Assert.Equal(-1, new StatsRowViewModel("x", -1, "-1").TotalTokens);
    }
}
