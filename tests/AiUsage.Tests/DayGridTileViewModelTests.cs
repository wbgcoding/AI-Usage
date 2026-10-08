using AiUsage.Services;
using AiUsage.Stats;
using System.Windows;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// <see cref="DayGridTileViewModel"/>'s own logic: the pure week-count math behind the widget tile's
/// density adaptation, and what <see cref="DayGridTileViewModel.Refresh"/> pulls out of a real <see
/// cref="StatsStore"/>. The visual embedding into <see cref="Views.Controls.DayGridTile"/> is covered
/// by the widget's own VM verification instead - nothing here touches a visual tree.
/// </summary>
public class DayGridTileViewModelTests
{
    [Fact]
    public void ComputeVisibleWeeks_mini_is_always_exactly_one_week_whatever_the_width()
    {
        Assert.Equal(1, DayGridTileViewModel.ComputeVisibleWeeks(TileDensity.Mini, 1000));
        Assert.Equal(1, DayGridTileViewModel.ComputeVisibleWeeks(TileDensity.Mini, 50));
    }

    [Fact]
    public void ComputeVisibleWeeks_has_no_cap_at_a_year()
    {
        // 22 px weekday column, then 11 px cells with 3 px gaps.
        var weeks = DayGridTileViewModel.ComputeVisibleWeeks(TileDensity.Full, 22 + 120 * 14 - 3);

        Assert.Equal(120, weeks);
    }

    [Fact]
    public void ComputeVisibleWeeks_never_drops_below_one_week_however_narrow()
    {
        var weeks = DayGridTileViewModel.ComputeVisibleWeeks(TileDensity.Full, 0);

        Assert.Equal(1, weeks);
    }

    [Fact]
    public void Refresh_with_no_store_reports_no_data()
    {
        var tile = new DayGridTileViewModel(null);

        tile.Refresh();

        Assert.False(tile.HasAnyData);
        Assert.Empty(tile.Days);
    }

    [Fact]
    public async Task Refresh_reads_the_same_store_the_stats_window_reads()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-refresh");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([new StatsRecord("codex", today, "modelA", "projA", 1000, 0, 0, 0)]);

        var tile = new DayGridTileViewModel(store);
        await tile.RefreshAsync();

        Assert.True(tile.HasAnyData);
        var todayEntry = Assert.Single(tile.Days, day => day.Day == today);
        Assert.Equal(1000, todayEntry.Total);
        Assert.Equal("codex", todayEntry.LeaderProviderId);
    }

    [Fact]
    public async Task Refresh_never_reports_data_ahead_of_the_shown_range_as_the_total()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-total-span");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var yearStart = new DateOnly(today.Year, 1, 1);
        store.AddDelta([
            new StatsRecord("codex", yearStart, "modelA", "projA", 5000, 0, 0, 0),
            new StatsRecord("codex", today, "modelA", "projA", 100, 0, 0, 0),
        ]);

        var tile = new DayGridTileViewModel(store) { Density = TileDensity.Mini };
        await tile.RefreshAsync();

        // Mini only ever shows the last seven days - the year-start record from months earlier must
        // never be folded into the header's own "shown span" total.
        Assert.DoesNotContain("5", tile.TotalText.Replace(",", "").Replace(".", ""));
    }

    [Fact]
    public void Mini_density_shows_only_the_last_seven_days()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tile = new DayGridTileViewModel(null) { Density = TileDensity.Mini };
        tile.Refresh();

        Assert.Equal(today.AddDays(-6), tile.RangeStart);
        Assert.Equal(today, tile.RangeEnd);
        Assert.True(tile.ShowAsStrip);
        tile.Density = TileDensity.Full;
        Assert.False(tile.ShowAsStrip);
    }

    private static DateOnly WeekStart(DateOnly day) => StatsAggregator.WeekStart(day);

    [Fact]
    public void Under_the_en_US_culture_the_week_still_starts_on_monday()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            var sunday = new DateOnly(2026, 10, 4);
            var tile = new DayGridTileViewModel(null, () => sunday) { Density = TileDensity.Full, AvailableWidth = 22 + 1 * 14 - 3 };
            tile.Refresh();

            Assert.Equal(new DateOnly(2026, 9, 28), tile.RangeStart);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(26)]
    [InlineData(53)]
    public void A_width_for_N_week_columns_shows_the_current_week_and_the_N_minus_1_before_it(int columns)
    {
        var today = new DateOnly(2026, 10, 2);
        // Full density: a 22 px weekday column, then 11 px cells with 3 px gaps.
        var tile = new DayGridTileViewModel(null, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + columns * 14 - 3 };
        tile.Refresh();

        Assert.Equal(WeekStart(today).AddDays(-(columns - 1) * 7), tile.RangeStart);
        Assert.Equal(today, tile.RangeEnd);
    }

    [Fact]
    public void One_more_column_of_width_adds_exactly_one_older_week()
    {
        var today = new DateOnly(2026, 10, 2);
        var tile = new DayGridTileViewModel(null, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + 20 * 14 - 4 };
        tile.Refresh();
        var before = tile.RangeStart;

        tile.AvailableWidth += 1;

        Assert.Equal(before.AddDays(-7), tile.RangeStart);
    }

    [Fact]
    public void A_very_wide_tile_reaches_back_past_one_year_of_weeks()
    {
        var today = new DateOnly(2026, 10, 2);
        var tile = new DayGridTileViewModel(null, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + 120 * 14 - 3 };
        tile.Refresh();

        Assert.Equal(WeekStart(today).AddDays(-119 * 7), tile.RangeStart);
        Assert.Equal(today, tile.RangeEnd);
    }

    [Fact]
    public async Task Days_cover_the_range_start_through_today_with_empty_days_before_the_first_record()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-unbounded");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2026, 10, 2);
        var firstRecord = today.AddDays(-100);
        store.AddDelta([new StatsRecord("claude", firstRecord, "m", "p", 400, 0, 0, 0)]);

        var tile = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + 120 * 14 - 3 };
        await tile.RefreshAsync();

        Assert.Equal(WeekStart(today).AddDays(-119 * 7), tile.RangeStart);
        Assert.True(tile.Days[0].Day <= tile.RangeStart);
        Assert.Equal(today, tile.Days[^1].Day);
        Assert.Equal(tile.Days.Count, tile.Days[^1].Day.DayNumber - tile.Days[0].Day.DayNumber + 1);
        var before = Assert.Single(tile.Days, day => day.Day == firstRecord.AddDays(-1));
        Assert.Equal(0, before.Total);
        Assert.Equal(400, Assert.Single(tile.Days, day => day.Day == firstRecord).Total);
    }

    [Fact]
    public async Task Widening_the_tile_after_a_refresh_rebuilds_the_days_without_the_store()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-widen");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2026, 10, 2);
        var old = today.AddDays(-700);
        store.AddDelta([new StatsRecord("claude", old, "m", "p", 900, 0, 0, 0)]);

        var tile = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 300 };
        await tile.RefreshAsync();
        Assert.True(tile.Days[0].Day > old);

        tile.AvailableWidth = 22 + 120 * 14 - 3;

        Assert.True(tile.Days[0].Day <= tile.RangeStart);
        Assert.Equal(900, Assert.Single(tile.Days, day => day.Day == old).Total);
    }

    [Fact]
    public async Task A_day_keeps_its_color_step_whatever_the_tile_width()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-quantile");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2026, 10, 2);
        var records = new List<StatsRecord>();
        for (var i = 0; i < 400; i++)
            records.Add(new StatsRecord("claude", today.AddDays(-i), "m", "p", 100 + i * 10, 0, 0, 0));
        store.AddDelta(records);

        var narrow = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + 10 * 14 - 3 };
        var wide = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 22 + 120 * 14 - 3 };
        await narrow.RefreshAsync();
        await wide.RefreshAsync();

        var total = 100 + 3 * 10;
        Assert.Equal(
            AiUsage.Views.Controls.StatsMonthGrid.QuantileStep(total, narrow.ColorScaleTotals!),
            AiUsage.Views.Controls.StatsMonthGrid.QuantileStep(total, wide.ColorScaleTotals!));
        Assert.Equal(400, wide.ColorScaleTotals!.Count);
        Assert.Equal(narrow.ColorScaleTotals, wide.ColorScaleTotals);
    }

    [Fact]
    public void A_refresh_after_midnight_raises_Today_so_the_bound_grid_moves_on()
    {
        var today = new DateOnly(2027, 3, 10);
        var tile = new DayGridTileViewModel(null, () => today) { Density = TileDensity.Full, AvailableWidth = 300 };
        tile.Refresh();
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        today = today.AddDays(1);
        tile.Refresh();

        Assert.Equal(today, tile.Today);
        Assert.Contains(nameof(DayGridTileViewModel.Today), raised);
    }

    [Fact]
    public void Early_in_january_the_range_reaches_into_the_previous_year()
    {
        var today = new DateOnly(2027, 1, 5);
        var tile = new DayGridTileViewModel(null, () => today) { Density = TileDensity.Full, AvailableWidth = 300 };
        tile.Refresh();

        Assert.True(tile.RangeStart < new DateOnly(2027, 1, 1));
        Assert.Equal(today, tile.RangeEnd);
    }

    [Fact]
    public async Task Refresh_loads_the_days_of_the_previous_year_the_earlier_columns_need()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-previous-year");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2027, 1, 5);
        var lastYear = new DateOnly(2026, 11, 20);
        store.AddDelta([new StatsRecord("claude", lastYear, "modelA", "projA", 700, 0, 0, 0)]);

        var tile = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 300 };
        await tile.RefreshAsync();

        Assert.Equal(700, Assert.Single(tile.Days, day => day.Day == lastYear).Total);
        Assert.True(tile.RangeStart <= lastYear);
    }

    [Fact]
    public async Task The_total_counts_only_the_shown_weeks_not_the_whole_loaded_year()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("daygrid-tile-total-shown");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2026, 10, 2);
        store.AddDelta([
            new StatsRecord("claude", today.AddDays(-300), "modelA", "projA", 5_000_000, 0, 0, 0),
            new StatsRecord("claude", today, "modelA", "projA", 1_000, 0, 0, 0),
        ]);

        var narrow = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 100 };
        await narrow.RefreshAsync();
        var wide = new DayGridTileViewModel(store, () => today) { Density = TileDensity.Full, AvailableWidth = 100_000 };
        await wide.RefreshAsync();

        Assert.NotEqual(narrow.TotalText, wide.TotalText);
    }

    [Fact]
    public void SelectDay_never_reports_a_future_day()
    {
        var tile = new DayGridTileViewModel(null);
        tile.Refresh();
        DateOnly? reported = null;
        tile.DaySelected += (_, day) => reported = day;

        tile.SelectDayCommand.Execute(tile.Today.AddDays(5));

        Assert.Null(reported);
    }

    [Fact]
    public void SelectDay_reports_todays_own_date()
    {
        var tile = new DayGridTileViewModel(null);
        tile.Refresh();
        DateOnly? reported = null;
        tile.DaySelected += (_, day) => reported = day;

        tile.SelectDayCommand.Execute(tile.Today);

        Assert.Equal(tile.Today, reported);
    }

    [Fact]
    public void ToggleVisibilityActionText_names_the_tile_and_the_opposite_action()
    {
        var tile = new DayGridTileViewModel(null) { IsHidden = false };

        Assert.Contains(LocalizationService.Instance["Stats.MonthGrid.Head"], tile.ToggleVisibilityActionText);
        Assert.Contains(LocalizationService.Instance["Tile.Hide"], tile.ToggleVisibilityActionText);

        tile.IsHidden = true;
        Assert.Contains(LocalizationService.Instance["Tile.Show"], tile.ToggleVisibilityActionText);
    }

    [Fact]
    public void Hide_raises_HideRequested()
    {
        var tile = new DayGridTileViewModel(null);
        var raised = false;
        tile.HideRequested += (_, _) => raised = true;

        tile.HideCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void ShowLegend_is_full_density_only()
    {
        var tile = new DayGridTileViewModel(null);

        tile.Density = TileDensity.Full;
        Assert.True(tile.ShowLegend);
        tile.Density = TileDensity.Mini;
        Assert.False(tile.ShowLegend);
    }

    [Fact]
    public void ShowAxisLabels_is_off_only_at_mini_density()
    {
        var tile = new DayGridTileViewModel(null);

        tile.Density = TileDensity.Full;
        Assert.True(tile.ShowAxisLabels);
        tile.Density = TileDensity.Mini;
        Assert.False(tile.ShowAxisLabels);
    }

    [Fact]
    public void Inner_width_leaves_out_padding_and_border()
    {
        var width = AiUsage.Views.Controls.DayGridTile.InnerWidth(340, new Thickness(12), new Thickness(1));

        Assert.Equal(314, width);
        Assert.Equal(0, AiUsage.Views.Controls.DayGridTile.InnerWidth(10, new Thickness(12), new Thickness(1)));
    }
}
