using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Coverage for the pure methods behind the statistics window's contribution-graph year
/// grid - the geometry, the future-day cutoff and the quantile shading, never the actual drawing,
/// which the existing <c>StatsWindowTests</c> visual walk already exercises end to end.</summary>
public class StatsMonthGridTests
{
    private static StatsMonthGrid.DayValue Day(int year, int month, int day, long total) =>
        new(new DateOnly(year, month, day), total);

    [Fact]
    public void WeekStart_walks_back_to_monday()
    {
        // 2026-09-26 is a Saturday.
        Assert.Equal(new DateOnly(2026, 9, 21), AiUsage.Stats.StatsAggregator.WeekStart(new DateOnly(2026, 9, 26)));
    }

    [Fact]
    public void WeekStart_of_a_sunday_is_the_monday_six_days_earlier()
    {
        Assert.Equal(new DateOnly(2026, 9, 28), AiUsage.Stats.StatsAggregator.WeekStart(new DateOnly(2026, 10, 4)));
    }

    [Fact]
    public void WeekStart_never_goes_below_the_first_calendar_day()
    {
        Assert.Equal(DateOnly.MinValue, AiUsage.Stats.StatsAggregator.WeekStart(DateOnly.MinValue));
    }

    [Fact]
    public void WeekStart_leaves_a_monday_untouched()
    {
        Assert.Equal(new DateOnly(2026, 9, 21), AiUsage.Stats.StatsAggregator.WeekStart(new DateOnly(2026, 9, 21)));
    }

    [Fact]
    public void BuildColumns_places_every_day_under_the_right_weekday_row()
    {
        var gridStart = new DateOnly(2026, 9, 21); // a Monday
        var rangeEnd = new DateOnly(2026, 12, 31);
        var today = new DateOnly(2026, 9, 27);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [], totalColumns);

        Assert.Equal(new DateOnly(2026, 9, 21), columns[0][0]!.Value.Day); // Monday - row 0
        Assert.Equal(new DateOnly(2026, 9, 27), columns[0][6]!.Value.Day); // Sunday - row 6
    }

    [Fact]
    public void BuildColumns_flags_a_day_after_today_as_future_rather_than_leaving_it_undrawn()
    {
        var gridStart = new DateOnly(2026, 9, 21);
        var rangeEnd = new DateOnly(2026, 12, 31); // the grid's own fixed span: the whole year
        var today = new DateOnly(2026, 9, 24); // Thursday - the rest of that week has not happened yet
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [], totalColumns);

        var thursday = columns[0][3]!.Value; // Thursday itself: today, not future
        Assert.False(thursday.IsFuture);

        var friday = columns[0][4]!.Value; // a future day - still drawn, just flagged
        Assert.True(friday.IsFuture);
        Assert.Equal(0, friday.Total);
    }

    [Fact]
    public void BuildColumns_leaves_a_day_past_the_year_undrawn()
    {
        var gridStart = new DateOnly(2026, 12, 28); // a Monday, the year's very last week
        var rangeEnd = new DateOnly(2026, 12, 31); // Thursday - the year itself ends there
        var today = new DateOnly(2026, 12, 31);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [], totalColumns);

        Assert.NotNull(columns[0][3]); // Dec 31 itself
        Assert.Null(columns[0][4]); // Jan 1 of next year - past the grid's own span, not a day of it
        Assert.Null(columns[0][6]);
    }

    [Fact]
    public void CellsOutsideTheSelectedPeriodAreDimmed()
    {
        var gridStart = new DateOnly(2026, 9, 21);
        var rangeEnd = new DateOnly(2026, 9, 27);
        var columns = StatsMonthGrid.BuildColumns(
            gridStart, rangeEnd, rangeEnd, [], 1, highlightStart: new DateOnly(2026, 9, 23), highlightEnd: new DateOnly(2026, 9, 26));

        Assert.True(columns[0][0]!.Value.IsOutsideHighlight); // before the period
        Assert.True(columns[0][1]!.Value.IsOutsideHighlight);
        Assert.False(columns[0][2]!.Value.IsOutsideHighlight); // first day of the period
        Assert.False(columns[0][5]!.Value.IsOutsideHighlight); // last day of the period
        Assert.True(columns[0][6]!.Value.IsOutsideHighlight); // after the period
    }

    [Fact]
    public void IntensityScaleUsesTheLeadingProvidersColour()
    {
        var neutral = System.Windows.Media.Colors.Gray;
        StatsMonthGrid.LegendEntry[] legend = [new("codex", 900), new("claude", 100)];

        Assert.Equal(AiUsage.Stats.ChartPalette.ForProvider("codex"), StatsMonthGrid.IntensityScaleColor(legend, neutral));
        Assert.Equal(neutral, StatsMonthGrid.IntensityScaleColor([], neutral));
    }

    [Fact]
    public void NoHighlightPeriodDimsNothing()
    {
        var gridStart = new DateOnly(2026, 9, 21);
        var rangeEnd = new DateOnly(2026, 9, 27);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, rangeEnd, [], 1);

        Assert.All(columns[0], cell => Assert.False(cell!.Value.IsOutsideHighlight));
    }

    [Fact]
    public void BuildColumns_draws_a_day_with_no_matching_record_as_an_empty_cell_not_a_gap()
    {
        var gridStart = new DateOnly(2026, 9, 21);
        var rangeEnd = new DateOnly(2026, 12, 31);
        var today = new DateOnly(2026, 9, 27);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [Day(2026, 9, 22, 500)], totalColumns);

        var untouchedDay = columns[0][0]!.Value; // Monday, no record at all
        Assert.Equal(0, untouchedDay.Total);
        Assert.Empty(untouchedDay.ByProvider);

        var recordedDay = columns[0][1]!.Value; // Tuesday
        Assert.Equal(500, recordedDay.Total);
    }

    [Fact]
    public void BuildColumns_carries_the_leader_provider_id_onto_the_cell()
    {
        var gridStart = new DateOnly(2026, 9, 21);
        var rangeEnd = new DateOnly(2026, 12, 31);
        var today = new DateOnly(2026, 9, 27);
        var day = new StatsMonthGrid.DayValue(
            new DateOnly(2026, 9, 22), 900, "codex",
            [new StatsMonthGrid.ProviderTotal("codex", 800), new StatsMonthGrid.ProviderTotal("claude", 100)]);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);

        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [day], totalColumns);

        Assert.Equal("codex", columns[0][1]!.Value.LeaderProviderId);
    }

    [Fact]
    public void SortedNonZeroTotals_counts_only_the_days_inside_the_drawn_range()
    {
        StatsMonthGrid.DayValue[] days =
        [
            new(new DateOnly(2026, 8, 1), 1_000_000),
            new(new DateOnly(2026, 9, 1), 10),
            new(new DateOnly(2026, 9, 2), 20),
            new(new DateOnly(2026, 9, 3), 0),
            new(new DateOnly(2026, 10, 1), 2_000_000),
        ];

        var totals = StatsMonthGrid.SortedNonZeroTotals(days, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Equal([10L, 20L], totals);
    }

    [Fact]
    public void The_accessible_summary_follows_a_change_of_the_range()
    {
        Exception? failure = null;
        string? name = null;
        var worker = new Thread(() =>
        {
            try
            {
                var grid = new StatsMonthGrid
                {
                    Days = [new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 5), 700)],
                };
                grid.RangeStart = new DateOnly(2026, 9, 1);
                grid.RangeEnd = new DateOnly(2026, 9, 30);
                name = AutomationProperties.GetName(grid);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "grid test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("grid test failed.", failure);

        Assert.Contains("700", name);
    }

    [Fact]
    public void QuantileStep_is_zero_without_usage()
    {
        Assert.Equal(0, StatsMonthGrid.QuantileStep(0, [10, 20, 30, 40]));
        Assert.Equal(0, StatsMonthGrid.QuantileStep(-5, [10, 20, 30, 40]));
        Assert.Equal(0, StatsMonthGrid.QuantileStep(10, []));
    }

    [Fact]
    public void QuantileStep_buckets_a_value_into_the_right_quarter_of_the_non_zero_days()
    {
        var sorted = new long[] { 10, 20, 30, 40, 50, 60, 70, 80 };

        Assert.Equal(1, StatsMonthGrid.QuantileStep(10, sorted)); // bottom quarter
        Assert.Equal(2, StatsMonthGrid.QuantileStep(40, sorted));
        Assert.Equal(3, StatsMonthGrid.QuantileStep(60, sorted));
        Assert.Equal(4, StatsMonthGrid.QuantileStep(80, sorted)); // busiest quarter
    }

    [Fact]
    public void FitColumns_keeps_the_default_size_while_every_column_fits()
    {
        var fit = StatsMonthGrid.FitColumns(10, 500, 3, StatsMonthGrid.DefaultCellSize, StatsMonthGrid.MinCellSize);

        Assert.Equal(StatsMonthGrid.DefaultCellSize, fit.CellSize);
        Assert.Equal(10, fit.VisibleColumns);
    }

    [Fact]
    public void FitColumns_shrinks_the_cell_size_before_going_below_the_minimum()
    {
        // 30 columns at the default size would need far more than 300px - shrinking still fits all 30.
        var fit = StatsMonthGrid.FitColumns(30, 300, 1, StatsMonthGrid.DefaultCellSize, StatsMonthGrid.MinCellSize);

        Assert.Equal(30, fit.VisibleColumns);
        Assert.True(fit.CellSize < StatsMonthGrid.DefaultCellSize);
        Assert.True(fit.CellSize >= StatsMonthGrid.MinCellSize);
    }

    [Fact]
    public void FitColumns_never_drops_a_column_even_when_the_minimum_size_does_not_fit()
    {
        // 53 columns, the full year, into a width far too small even at the minimum cell size - every
        // column still gets drawn, wider than the available space; the caller scrolls to the rest
        // instead of ever losing a week off the grid.
        var fit = StatsMonthGrid.FitColumns(53, 80, 1, StatsMonthGrid.DefaultCellSize, StatsMonthGrid.MinCellSize);

        Assert.Equal(StatsMonthGrid.MinCellSize, fit.CellSize);
        Assert.Equal(53, fit.VisibleColumns);
    }

    [Fact]
    public void MonthLabels_labels_the_first_column_and_every_column_where_a_new_month_starts()
    {
        var gridStart = new DateOnly(2026, 6, 1); // a Monday, and June's own first day
        var rangeEnd = new DateOnly(2026, 9, 19);
        var today = rangeEnd;
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);
        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [], totalColumns);

        var labels = StatsMonthGrid.MonthLabels(columns, new CultureInfo("en-US"));

        Assert.Equal(0, labels[0].Column);
        Assert.Equal("Jun", labels[0].Label);
        Assert.Contains(labels, entry => entry.Label == "Jul");
        Assert.Contains(labels, entry => entry.Label == "Aug");
        Assert.Contains(labels, entry => entry.Label == "Sep");
    }

    [Fact]
    public void MonthLabels_use_the_standalone_abbreviation_in_german()
    {
        var gridStart = new DateOnly(2026, 8, 31); // a Monday
        var rangeEnd = new DateOnly(2026, 11, 30);
        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, rangeEnd, [], StatsMonthGrid.TotalColumns(gridStart, rangeEnd));

        var labels = StatsMonthGrid.MonthLabels(columns, new CultureInfo("de-DE"));

        Assert.Contains(labels, entry => entry.Label == "Sep");
        Assert.Contains(labels, entry => entry.Label == "Okt");
        Assert.DoesNotContain(labels, entry => entry.Label.StartsWith("Sept", StringComparison.Ordinal));
    }

    [Fact]
    public void MonthLabels_skips_a_month_whose_own_label_would_sit_too_close_to_the_one_before_it()
    {
        // The grid's very first (partial) column falls in August, and September starts only one
        // column later - too close to also label without the two texts overlapping.
        var gridStart = new DateOnly(2026, 8, 24); // a Monday, mid-August
        var rangeEnd = new DateOnly(2026, 9, 6);
        var today = rangeEnd;
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);
        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, today, [], totalColumns);

        var labels = StatsMonthGrid.MonthLabels(columns, new CultureInfo("en-US"));

        Assert.Equal(0, labels[0].Column);
        Assert.Equal("Aug", labels[0].Label);
        Assert.DoesNotContain(labels, entry => entry.Label == "Sep");
    }

    [Fact]
    public void MonthLabels_add_the_year_to_January_once_the_grid_spans_more_than_a_year()
    {
        var gridStart = new DateOnly(2025, 6, 2); // a Monday
        var rangeEnd = new DateOnly(2026, 10, 8);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);
        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, rangeEnd, [], totalColumns);

        var labels = StatsMonthGrid.MonthLabels(columns, new CultureInfo("en-US"));

        Assert.Contains(labels, entry => entry.Label == "Jan 26");
        Assert.DoesNotContain(labels, entry => entry.Label == "Jan");
        Assert.Contains(labels, entry => entry.Label == "Oct");
    }

    [Fact]
    public void MonthLabels_keep_January_plain_within_twelve_months()
    {
        var gridStart = new DateOnly(2025, 10, 6); // a Monday
        var rangeEnd = new DateOnly(2026, 10, 3);
        var totalColumns = StatsMonthGrid.TotalColumns(gridStart, rangeEnd);
        var columns = StatsMonthGrid.BuildColumns(gridStart, rangeEnd, rangeEnd, [], totalColumns);

        var labels = StatsMonthGrid.MonthLabels(columns, new CultureInfo("en-US"));

        Assert.Contains(labels, entry => entry.Label == "Jan");
    }

    [Fact]
    public void BuildAccessibleSummary_names_the_covered_range_and_its_total()
    {
        var days = new[] { Day(2026, 9, 1, 100), Day(2026, 9, 2, 50) };

        var summary = StatsMonthGrid.BuildAccessibleSummary(days, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), new CultureInfo("en-US"));

        Assert.Equal("9/1-9/2/2026: 150", summary);
    }

    [Fact]
    public void BuildAccessibleSummary_is_empty_for_an_inverted_range()
    {
        var summary = StatsMonthGrid.BuildAccessibleSummary([], new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 1), CultureInfo.InvariantCulture);

        Assert.Equal("", summary);
    }

    [Fact]
    public void BuildProviderLegend_lists_every_leading_provider_largest_first()
    {
        var days = new[]
        {
            new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 1), 900, "codex"),
            new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 2), 100, "claude"),
            new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 3), 300, "claude"),
            new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 4), 0, ""), // no usage: no leader
        };

        var legend = StatsMonthGrid.BuildProviderLegend(days);

        Assert.Equal(["codex", "claude"], legend.Select(entry => entry.ProviderId));
        Assert.Equal(400, legend.Single(entry => entry.ProviderId == "claude").Total);
    }

    [Fact]
    public void BuildProviderLegend_is_empty_for_a_year_with_no_usage_at_all()
    {
        var legend = StatsMonthGrid.BuildProviderLegend([Day(2026, 9, 1, 0)]);

        Assert.Empty(legend);
    }

    [Fact]
    public void BuildTooltipLines_shows_the_date_and_weekday_then_every_provider_then_the_total()
    {
        var providerNames = new Dictionary<string, string> { ["claude"] = "Claude", ["codex"] = "Codex" };
        var hover = new StatsMonthGrid.CellHit(
            default, new DateOnly(2026, 9, 26), 1000,
            [new StatsMonthGrid.ProviderTotal("codex", 900), new StatsMonthGrid.ProviderTotal("claude", 100)]);

        var lines = StatsMonthGrid.BuildTooltipLines(hover, providerNames, "tokens", "Total", "no usage", new CultureInfo("en-US"));

        Assert.Equal(
        [
            "9/26/2026 Sat",
            "Codex: 900 tokens",
            "Claude: 100 tokens",
            "Total: 1,000 tokens",
        ], lines);
    }

    [Fact]
    public void BuildTooltipLines_falls_back_to_the_raw_provider_id_when_no_display_name_is_known()
    {
        var hover = new StatsMonthGrid.CellHit(default, new DateOnly(2026, 9, 26), 5, [new StatsMonthGrid.ProviderTotal("cursor", 5)]);

        var lines = StatsMonthGrid.BuildTooltipLines(hover, new Dictionary<string, string>(), "", "", "no usage", CultureInfo.InvariantCulture);

        Assert.Contains("cursor: 5", lines);
    }

    [Fact]
    public void BuildTooltipLines_shows_the_no_usage_text_for_an_empty_day()
    {
        var hover = new StatsMonthGrid.CellHit(default, new DateOnly(2026, 9, 26), 0, []);

        var lines = StatsMonthGrid.BuildTooltipLines(hover, new Dictionary<string, string>(), "tokens", "Total", "no usage", new CultureInfo("en-US"));

        Assert.Equal(["9/26/2026 Sat", "no usage"], lines);
    }

    // ShowLegend/ShowAxisLabels back the widget's own day-grid tile (DayGridTileViewModel): Compact
    // drops the legend row, Mini drops both the legend and the weekday/month labels, so the tile at
    // its narrowest never asks for more height than the bare grid of cells needs.
    [Fact]
    public void Hiding_the_legend_shrinks_the_desired_height()
    {
        var (withLegend, withoutLegend) = RunOnSta(() =>
        {
            var days = new[] { Day(2026, 9, 1, 100) };
            double Measure(bool showLegend)
            {
                var grid = new StatsMonthGrid
                {
                    Days = days,
                    RangeStart = new DateOnly(2026, 9, 1),
                    RangeEnd = new DateOnly(2026, 9, 26),
                    Today = new DateOnly(2026, 9, 26),
                    AvailableWidth = 400,
                    ShowLegend = showLegend,
                };
                grid.Measure(new Size(400, double.PositiveInfinity));
                return grid.DesiredSize.Height;
            }
            return (Measure(true), Measure(false));
        });

        Assert.True(withoutLegend < withLegend);
    }

    [Fact]
    public void Hiding_the_axis_labels_shrinks_the_desired_size()
    {
        var (withLabels, withoutLabels) = RunOnSta(() =>
        {
            var days = new[] { Day(2026, 9, 1, 100) };
            Size Measure(bool showAxisLabels)
            {
                var grid = new StatsMonthGrid
                {
                    Days = days,
                    RangeStart = new DateOnly(2026, 9, 1),
                    RangeEnd = new DateOnly(2026, 9, 26),
                    Today = new DateOnly(2026, 9, 26),
                    AvailableWidth = 400,
                    ShowLegend = false,
                    ShowAxisLabels = showAxisLabels,
                };
                grid.Measure(new Size(400, double.PositiveInfinity));
                return grid.DesiredSize;
            }
            return (Measure(true), Measure(false));
        });

        Assert.True(withoutLabels.Width < withLabels.Width);
        Assert.True(withoutLabels.Height < withLabels.Height);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "measure did not finish");
        if (failure is not null)
            throw failure;
        return result!;
    }

    [Fact]
    public void Builder_spans_the_twelve_months_up_to_today_and_no_further()
    {
        var today = new DateOnly(2027, 1, 3);

        var result = AiUsage.Stats.StatsMonthGridBuilder.Build([], today);

        Assert.Equal(new DateOnly(2026, 2, 1), result.RangeStart);
        Assert.Equal(today, result.RangeEnd);
        Assert.Equal(today, result.Days[^1].Day);
    }

    [Fact]
    public void Builder_color_scale_covers_the_whole_history_not_only_the_returned_days()
    {
        var today = new DateOnly(2027, 1, 3);
        var records = new[]
        {
            new AiUsage.Stats.StatsRecord("claude", new DateOnly(2023, 5, 1), "m", "p", 700, 0, 0, 0),
            new AiUsage.Stats.StatsRecord("claude", new DateOnly(2023, 5, 1), "m", "p", 100, 0, 0, 0),
            new AiUsage.Stats.StatsRecord("codex", today, "m", "p", 50, 0, 0, 0),
        };

        var result = AiUsage.Stats.StatsMonthGridBuilder.Build(records, today);

        Assert.Equal(new long[] { 50, 800 }, result.ColorScaleTotals);
        Assert.DoesNotContain(result.Days, day => day.Day.Year == 2023);
    }

    [Fact]
    public void An_explicit_color_scale_wins_over_the_drawn_span()
    {
        var days = new[] { new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 1), 10) };
        var scale = new long[] { 1, 2, 3, 4 };

        Assert.Same(scale, StatsMonthGrid.ResolveColorScale(scale, days, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        Assert.Equal(new long[] { 10 }, StatsMonthGrid.ResolveColorScale(null, days, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void Month_labels_run_across_several_years_one_per_month()
    {
        var start = AiUsage.Stats.StatsAggregator.WeekStart(new DateOnly(2023, 11, 20));
        var end = new DateOnly(2026, 2, 10);
        var columns = StatsMonthGrid.BuildColumns(start, end, end, [], StatsMonthGrid.TotalColumns(start, end));

        var labels = StatsMonthGrid.MonthLabels(columns, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("Nov", labels[0].Label);
        Assert.Equal(["Jan 24", "Jan 25", "Jan 26"], labels.Where(label => label.Label.StartsWith("Jan")).Select(label => label.Label));
        Assert.True(labels.Count >= 24);
    }

    [Fact]
    public void Builder_reaches_before_the_twelve_months_when_asked_to()
    {
        var today = new DateOnly(2027, 1, 3);
        var earlier = new DateOnly(2025, 12, 29);
        var records = new[] { new AiUsage.Stats.StatsRecord("claude", earlier, "m", "p", 700, 0, 0, 0) };

        var days = AiUsage.Stats.StatsMonthGridBuilder.Build(records, today, from: earlier).Days;

        Assert.Equal(earlier, days[0].Day);
        Assert.Equal(700, days.Single(day => day.Day == earlier).Total);
    }

    [Fact]
    public void BuildStrip_puts_every_day_in_its_own_column_on_the_first_row()
    {
        var start = new DateOnly(2026, 9, 20);
        var end = new DateOnly(2026, 9, 26);
        var days = new[] { new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 22), 500) };

        var columns = StatsMonthGrid.BuildStrip(start, end, end, days);

        Assert.Equal(7, columns.Count);
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(start.AddDays(i), columns[i][0]!.Value.Day);
            Assert.All(columns[i].Skip(1), cell => Assert.Null(cell));
        }
        Assert.Equal(500, columns[2][0]!.Value.Total);
        Assert.False(columns[6][0]!.Value.IsFuture);
    }

    [Fact]
    public void IntensityBrush_hands_out_one_frozen_brush_per_color_and_step()
    {
        var color = Color.FromRgb(10, 120, 200);

        var first = StatsMonthGrid.IntensityBrush(color, 3);
        var again = StatsMonthGrid.IntensityBrush(color, 3);
        var other = StatsMonthGrid.IntensityBrush(color, 4);

        Assert.Same(first, again);
        Assert.NotSame(first, other);
        Assert.True(first.IsFrozen);
        Assert.Equal(Color.FromArgb(191, 10, 120, 200), first.Color);
        Assert.Equal(Color.FromArgb(255, 10, 120, 200), other.Color);
    }
}
