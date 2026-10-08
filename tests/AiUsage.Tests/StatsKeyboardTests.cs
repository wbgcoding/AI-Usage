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
}
