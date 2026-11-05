using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Keyboard access to the statistics charts: the month grid picks a day with the arrow
/// keys and Enter or Space like a mouse click does, the bar chart steps through its bars and names
/// the focused one for a screen reader.</summary>
public class StatsKeyboardTests
{
    private static void PressKey(UIElement target, Key key)
    {
        using var source = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "keyboard-test", IntPtr.Zero);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)), "keyboard test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("keyboard test failed.", failure);
        return result!;
    }

    private static StatsMonthGrid SeptemberGrid(bool singleRow) => new()
    {
        Days = [new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 2), 700, "claude", [new StatsMonthGrid.ProviderTotal("claude", 700)])],
        RangeStart = new DateOnly(2026, 9, 1),
        RangeEnd = new DateOnly(2026, 9, 30),
        Today = new DateOnly(2026, 9, 30),
        SingleRow = singleRow,
        AvailableWidth = 800,
    };

    [Fact]
    public void Month_grid_right_then_enter_selects_the_second_day()
    {
        var picked = RunOnSta(() =>
        {
            var grid = SeptemberGrid(singleRow: true);
            var days = new List<DateOnly>();
            grid.DaySelected += (_, day) => days.Add(day);

            PressKey(grid, Key.Right);
            PressKey(grid, Key.Enter);
            return days;
        });

        Assert.Equal([new DateOnly(2026, 9, 2)], picked);
    }

    [Fact]
    public void Month_grid_space_selects_the_focused_day_and_names_it_for_a_screen_reader()
    {
        var (picked, name) = RunOnSta(() =>
        {
            var grid = SeptemberGrid(singleRow: true);
            var days = new List<DateOnly>();
            grid.DaySelected += (_, day) => days.Add(day);

            PressKey(grid, Key.Right);
            PressKey(grid, Key.Space);
            return (days, AutomationProperties.GetName(grid));
        });

        Assert.Equal([new DateOnly(2026, 9, 2)], picked);
        Assert.Contains("700", name);
    }

    [Fact]
    public void Month_grid_enter_without_a_focused_day_selects_nothing()
    {
        var picked = RunOnSta(() =>
        {
            var grid = SeptemberGrid(singleRow: true);
            var days = new List<DateOnly>();
            grid.DaySelected += (_, day) => days.Add(day);

            PressKey(grid, Key.Enter);
            return days;
        });

        Assert.Empty(picked);
    }

    [Fact]
    public void Month_grid_right_steps_a_week_ahead_in_the_week_layout_and_down_a_day()
    {
        // 2026-09-01 is a Tuesday; a Monday-first grid puts it in row 1 of the first column.
        var columns = StatsMonthGrid.BuildColumns(
            new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30), [], 5);
        var first = new DateOnly(2026, 9, 1);

        Assert.Equal(new DateOnly(2026, 9, 8), StatsMonthGrid.MoveFocus(columns, first, Key.Right, singleRow: false));
        Assert.Equal(new DateOnly(2026, 9, 2), StatsMonthGrid.MoveFocus(columns, first, Key.Down, singleRow: false));
        Assert.Equal(new DateOnly(2026, 9, 1), StatsMonthGrid.MoveFocus(columns, first, Key.Left, singleRow: false));
        Assert.Equal(new DateOnly(2026, 9, 1), StatsMonthGrid.MoveFocus(columns, new DateOnly(2026, 9, 2), Key.Up, singleRow: false));
    }

    [Fact]
    public void Month_grid_focus_never_reaches_a_future_day()
    {
        var columns = StatsMonthGrid.BuildColumns(
            new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 10), [], 5);

        Assert.Equal(new DateOnly(2026, 9, 10), StatsMonthGrid.MoveFocus(columns, new DateOnly(2026, 9, 10), Key.Down, singleRow: false));
        Assert.Equal(new DateOnly(2026, 9, 10), StatsMonthGrid.MoveFocus(columns, new DateOnly(2026, 9, 3), Key.End, singleRow: false));
    }

    private static StatsBarChart ThreeBars() => new()
    {
        Bars =
        [
            new StatsBarChart.Bar("2026-09-01", [100]),
            new StatsBarChart.Bar("2026-09-02", [200]),
            new StatsBarChart.Bar("2026-09-03", [300]),
        ],
    };

    [Fact]
    public void Bar_chart_right_moves_the_focused_bar_and_names_it()
    {
        var (index, name) = RunOnSta(() =>
        {
            var chart = ThreeBars();

            PressKey(chart, Key.Right);
            PressKey(chart, Key.Right);
            return (chart.FocusedIndex, AutomationProperties.GetName(chart));
        });

        Assert.Equal(2, index);
        Assert.Contains("300", name);
    }

    [Fact]
    public void Bar_chart_focus_stays_inside_the_bars()
    {
        Assert.Equal(0, StatsBarChart.MoveFocusedIndex(0, 3, Key.Left));
        Assert.Equal(2, StatsBarChart.MoveFocusedIndex(2, 3, Key.Right));
        Assert.Equal(2, StatsBarChart.MoveFocusedIndex(0, 3, Key.End));
        Assert.Equal(0, StatsBarChart.MoveFocusedIndex(2, 3, Key.Home));
        Assert.Equal(-1, StatsBarChart.MoveFocusedIndex(0, 0, Key.Right));
    }

    [Fact]
    public void Both_charts_are_focusable()
    {
        var focusable = RunOnSta(() => (new StatsMonthGrid().Focusable, new StatsBarChart().Focusable));

        Assert.True(focusable.Item1);
        Assert.True(focusable.Item2);
    }

    private static StatsRingChart ThreeSlices(bool activatable) => new()
    {
        IsActivatable = activatable,
        Slices = [new StatsRingChart.Slice("Opus", 60), new StatsRingChart.Slice("Sonnet", 0), new StatsRingChart.Slice("Haiku", 40)],
    };

    [Fact]
    public void Ring_chart_is_focusable_only_while_it_is_activatable()
    {
        var focusable = RunOnSta(() => (ThreeSlices(false).Focusable, ThreeSlices(true).Focusable));

        Assert.False(focusable.Item1);
        Assert.True(focusable.Item2);
    }

    [Fact]
    public void Ring_chart_right_skips_a_slice_without_a_share_and_names_the_focused_slice()
    {
        var (index, name) = RunOnSta(() =>
        {
            var ring = ThreeSlices(true);
            PressKey(ring, Key.Right);
            return (ring.FocusedIndex, AutomationProperties.GetName(ring));
        });

        Assert.Equal(2, index);
        Assert.Contains("Haiku", name);
    }

    [Fact]
    public void Ring_chart_enter_and_space_activate_the_focused_slice()
    {
        var picked = RunOnSta(() =>
        {
            var ring = ThreeSlices(true);
            var indices = new List<int>();
            ring.SliceActivated += (_, index) => indices.Add(index);

            PressKey(ring, Key.Right);
            PressKey(ring, Key.Enter);
            PressKey(ring, Key.Left);
            PressKey(ring, Key.Space);
            return indices;
        });

        Assert.Equal([2, 0], picked);
    }

    [Fact]
    public void Ring_chart_keys_do_nothing_while_it_is_not_activatable()
    {
        var (index, handled) = RunOnSta(() =>
        {
            var ring = ThreeSlices(false);
            var indices = new List<int>();
            ring.SliceActivated += (_, i) => indices.Add(i);
            PressKey(ring, Key.Right);
            PressKey(ring, Key.Enter);
            return (ring.FocusedIndex, indices.Count);
        });

        Assert.Equal(-1, index);
        Assert.Equal(0, handled);
    }

    [Fact]
    public void Ring_chart_focus_stays_on_slices_that_have_a_share()
    {
        StatsRingChart.Slice[] slices = [new("A", 0), new("B", 50), new("C", 0), new("D", 50)];

        Assert.Equal(1, StatsRingChart.MoveFocusedIndex(-1, slices, Key.Home));
        Assert.Equal(3, StatsRingChart.MoveFocusedIndex(1, slices, Key.Right));
        Assert.Equal(3, StatsRingChart.MoveFocusedIndex(3, slices, Key.Right));
        Assert.Equal(1, StatsRingChart.MoveFocusedIndex(3, slices, Key.Left));
        Assert.Equal(3, StatsRingChart.MoveFocusedIndex(0, slices, Key.End));
        Assert.Equal(1, StatsRingChart.MoveFocusedIndex(2, slices, Key.None));
        Assert.Equal(-1, StatsRingChart.MoveFocusedIndex(0, [new StatsRingChart.Slice("A", 0)], Key.Right));
    }

    [Fact]
    public void Ring_chart_slice_anchor_sits_on_the_slices_own_arc()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 60), new("Codex", 40)];
        var anchor = StatsRingChart.SliceAnchor(1, 400, 132, slices);

        Assert.Equal(1, StatsRingChart.SliceIndexAt(anchor, 400, 132, slices));
    }
}
