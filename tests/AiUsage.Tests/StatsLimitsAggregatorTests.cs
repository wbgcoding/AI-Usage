using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class StatsLimitsAggregatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static HistoryPoint Point(WindowKind window, double percent, DateTimeOffset? resets, int hour = 0, string? label = null) =>
        new(1, Start.AddHours(hour), window, percent, resets, Label: label);

    private static IReadOnlyList<StatsLimitRow> Build(params (string, IReadOnlyList<HistoryPoint>)[] series) =>
        StatsLimitsAggregator.Build(series);

    [Fact]
    public void Two_periods_one_at_the_limit_count_one_reach_and_average_the_peaks()
    {
        var firstReset = Start.AddDays(7);
        var secondReset = Start.AddDays(14);
        var rows = Build(("claude", new[]
        {
            Point(WindowKind.Weekly, 40, firstReset, 0),
            Point(WindowKind.Weekly, 100, firstReset, 10),
            Point(WindowKind.Weekly, 90, firstReset, 20),
            Point(WindowKind.Weekly, 20, secondReset, 200),
            Point(WindowKind.Weekly, 60, secondReset, 210),
        }));

        var row = Assert.Single(rows);
        Assert.Equal("claude", row.ProviderId);
        Assert.Equal(2, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal(100, row.Highest);
        Assert.Equal(80, row.AveragePeak);
        Assert.Equal("Window_Weekly", row.WindowLabelKey);
    }

    [Fact]
    public void A_wobbling_reset_time_stays_one_period()
    {
        var reset = Start.AddHours(5);
        var rows = Build(("codex", new[]
        {
            Point(WindowKind.FiveHour, 10, reset, 0),
            Point(WindowKind.FiveHour, 30, reset.AddSeconds(4), 1),
            Point(WindowKind.FiveHour, 50, reset.AddSeconds(-3), 2),
        }));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.PeriodCount);
        Assert.Equal(0, row.ReachedCount);
        Assert.Equal(50, row.AveragePeak);
    }

    [Fact]
    public void Points_without_a_reset_time_form_one_period()
    {
        var rows = Build(("cursor", new[]
        {
            Point(WindowKind.Other, 100, null, 0, "Window_Month"),
            Point(WindowKind.Other, 100, null, 5, "Window_Month"),
        }));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal("Window_Month", row.WindowLabelKey);
    }

    [Fact]
    public void Windows_of_one_provider_are_separate_rows_ordered_five_hour_weekly_other()
    {
        var reset = Start.AddDays(1);
        var rows = Build(
            ("claude", new[]
            {
                Point(WindowKind.Weekly, 30, reset),
                Point(WindowKind.FiveHour, 10, reset),
            }),
            ("codex", new[] { Point(WindowKind.Other, 5, reset, label: "Window_CursorModels") }));

        Assert.Equal(
            [("claude", WindowKind.FiveHour), ("claude", WindowKind.Weekly), ("codex", WindowKind.Other)],
            rows.Select(row => (row.ProviderId, row.Window)));
    }

    [Fact]
    public void A_row_names_its_first_and_last_reading_and_when_it_last_hit_the_limit()
    {
        var firstReset = Start.AddDays(7);
        var secondReset = Start.AddDays(14);
        var row = Assert.Single(Build(("claude", new[]
        {
            Point(WindowKind.Weekly, 40, firstReset, 5),
            Point(WindowKind.Weekly, 100, firstReset, 10),
            Point(WindowKind.Weekly, 100, firstReset, 12),
            Point(WindowKind.Weekly, 70, firstReset, 20),
            Point(WindowKind.Weekly, 30, secondReset, 200),
        })));

        Assert.Equal(Start.AddHours(5), row.FirstReading);
        Assert.Equal(Start.AddHours(200), row.LastReading);
        Assert.Equal(Start.AddHours(12), row.LastReachedAt);
    }

    [Fact]
    public void A_window_that_never_hit_the_limit_has_no_last_reached_time_and_no_blocked_time()
    {
        var row = Assert.Single(Build(("claude", new[] { Point(WindowKind.Weekly, 99.9, Start.AddDays(7), 3) })));

        Assert.Null(row.LastReachedAt);
        Assert.Equal(TimeSpan.Zero, row.BlockedTime);
    }

    [Fact]
    public void Blocked_time_runs_from_the_first_hit_of_a_period_to_its_reset()
    {
        var firstReset = Start.AddDays(7);
        var secondReset = Start.AddDays(14);
        var row = Assert.Single(Build(("claude", new[]
        {
            // Hits at 90 minutes before the reset and stays there: 90 minutes blocked.
            Point(WindowKind.Weekly, 60, firstReset, 100),
            new HistoryPoint(1, firstReset.AddMinutes(-90), WindowKind.Weekly, 100, firstReset),
            new HistoryPoint(1, firstReset.AddMinutes(-30), WindowKind.Weekly, 100, firstReset),
            // A period that never hit the limit adds nothing.
            Point(WindowKind.Weekly, 50, secondReset, 200),
        })));

        Assert.Equal(2, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal(TimeSpan.FromMinutes(90), row.BlockedTime);
    }

    [Fact]
    public void Blocked_time_is_clamped_to_the_windows_own_length_and_never_negative()
    {
        var hit = Start.AddHours(1);
        var fiveHour = Assert.Single(Build(("claude", new[] { Point(WindowKind.FiveHour, 100, hit.AddHours(9), 1) })));
        var weekly = Assert.Single(Build(("claude", new[] { Point(WindowKind.Weekly, 100, hit.AddDays(12), 1) })));
        var other = Assert.Single(Build(("claude", new[] { Point(WindowKind.Other, 100, hit.AddDays(12), 1) })));
        var behind = Assert.Single(Build(("claude", new[] { Point(WindowKind.Weekly, 100, hit.AddMinutes(-20), 1) })));

        Assert.Equal(TimeSpan.FromHours(5), fiveHour.BlockedTime);
        Assert.Equal(TimeSpan.FromDays(7), weekly.BlockedTime);
        Assert.Equal(TimeSpan.FromDays(12), other.BlockedTime);
        Assert.Equal(TimeSpan.Zero, behind.BlockedTime);
    }

    [Fact]
    public void A_period_without_a_reset_time_is_not_blocked_for_any_time()
    {
        var row = Assert.Single(Build(("claude", new[] { Point(WindowKind.Weekly, 100, null, 1) })));

        Assert.Equal(1, row.ReachedCount);
        Assert.Equal(TimeSpan.Zero, row.BlockedTime);
        Assert.NotNull(row.LastReachedAt);
    }

    private static HistoryPoint At(int minute, double percent, DateTimeOffset reset, string? plan = null, bool? early = null) =>
        new(1, Start.AddMinutes(minute), WindowKind.Weekly, percent, reset, Plan: plan, EarlyReset: early);

    [Fact]
    public void An_early_reset_ends_the_cycle_and_blocks_only_until_the_reset()
    {
        var reset = Start.AddDays(4);
        var row = Assert.Single(Build(("claude", new[]
        {
            At(0, 60, reset),
            At(600, 100, reset),
            At(660, 100, reset),
            At(664, 4, reset, early: true),
            At(900, 20, reset),
        })));

        Assert.Equal(2, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal(1, row.EarlyResetCount);
        Assert.Equal(Start.AddMinutes(664), row.LastEarlyResetAt);
        // Hit at minute 600, cleared at minute 664 (not at the reset days later).
        Assert.Equal(TimeSpan.FromMinutes(64), row.BlockedTime);
        Assert.Equal(100, row.Highest);
        Assert.Equal(60, row.AveragePeak);
    }

    [Fact]
    public void An_early_reset_without_the_stored_flag_is_found_from_the_values()
    {
        var reset = Start.AddDays(4);
        var row = Assert.Single(Build(("claude", new[]
        {
            At(0, 100, reset),
            At(4, 4, reset),
            At(100, 8, reset),
        })));

        Assert.Equal(2, row.PeriodCount);
        Assert.Equal(1, row.EarlyResetCount);
    }

    [Fact]
    public void A_regular_reset_and_a_plan_change_are_no_limit_resets()
    {
        var first = Start.AddDays(4);
        var second = first.AddDays(7);
        var row = Assert.Single(Build(("claude", new[]
        {
            At(0, 83, first, "Max"),
            At(2, 0, first, "Ultra"),
            At(5000, 50, second, "Ultra"),
            At(12000, 3, second.AddDays(7), "Ultra"),
        })));

        Assert.Equal(0, row.EarlyResetCount);
        Assert.Null(row.LastEarlyResetAt);
        Assert.Equal(4, row.PeriodCount);
    }

    [Fact]
    public void Splitting_into_periods_never_keeps_a_value_drop_inside_one_period()
    {
        var reset = Start.AddDays(4);
        var periods = StatsLimitsAggregator.SplitIntoPeriods(new[] { At(0, 100, reset), At(4, 4, reset), At(10, 9, reset) });

        Assert.Equal(2, periods.Count);
        Assert.Equal([100.0], periods[0].Select(point => point.Percent));
        Assert.Equal([4.0, 9.0], periods[1].Select(point => point.Percent));
    }

    [Fact]
    public void A_provider_without_points_gives_no_row()
    {
        Assert.Empty(Build(("claude", Array.Empty<HistoryPoint>())));
    }
}

public class StatsViewModelLimitsTests
{
    private static HistoryPoint Weekly(double percent, DateTimeOffset reset, int minutes = 0) =>
        new(1, DateTimeOffset.Now.AddHours(-3).AddMinutes(minutes), WindowKind.Weekly, percent, reset);

    private static StatsViewModel Create(Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>> history)
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-limits-vm");
        var viewModel = new StatsViewModel(new StatsStore(directory)) { LoadHistory = history };
        viewModel.Recompute();
        return viewModel;
    }

    [Fact]
    public void Quota_history_shows_one_row_per_window_with_the_formatted_figures()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id == "claude" ? [Weekly(40, reset), Weekly(100, reset, 10)] : []);

        var group = Assert.Single(viewModel.LimitGroups);
        Assert.Equal("Claude", group.ProviderName);
        var row = Assert.Single(viewModel.LimitGroups.SelectMany(group => group.Rows));
        Assert.DoesNotContain("Claude", row.Label, StringComparison.Ordinal);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal("Crit", row.Level);
        Assert.Equal(100, row.HighestPercent);
        Assert.Equal(StatusTextMap.FormatPercent("100"), row.HighestText);
        Assert.Equal(StatusTextMap.FormatPercent("100"), row.AveragePeakText);
        Assert.True(viewModel.HasLimitRows);
        Assert.False(viewModel.HasNoLimitRows);
    }

    [Fact]
    public void No_history_leaves_the_section_empty()
    {
        var viewModel = Create((_, _, _) => []);

        Assert.Empty(viewModel.LimitGroups.SelectMany(group => group.Rows));
        Assert.True(viewModel.HasNoLimitRows);
    }

    [Fact]
    public void The_provider_filter_limits_the_rows_to_that_provider()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id is "claude" or "codex" ? [Weekly(50, reset)] : []);
        Assert.Equal(2, viewModel.LimitGroups.SelectMany(group => group.Rows).Count());

        viewModel.SetProviderCommand.Execute("codex");

        Assert.Single(viewModel.LimitGroups.SelectMany(group => group.Rows));
        Assert.Equal(StatsViewModel.ProviderDisplayNames["codex"], Assert.Single(viewModel.LimitGroups).ProviderName);
    }

    [Fact]
    public void The_limits_show_one_provider_at_a_time_defaulting_to_the_first_with_rows()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id is "claude" or "codex" ? [Weekly(50, reset)] : []);

        Assert.Equal(["claude", "codex"], viewModel.LimitButtons.Select(button => button.ProviderId));
        Assert.Equal("claude", viewModel.SelectedLimitProvider);
        Assert.Equal("claude", Assert.Single(viewModel.LimitButtons, button => button.IsChecked).ProviderId);
        Assert.True(viewModel.HasLimitChoice);
        Assert.False(viewModel.HasSingleLimitProvider);
        Assert.Equal("claude", Assert.Single(viewModel.ShownLimitGroups).ProviderId);
        Assert.True(viewModel.HasLimitRows);
    }

    [Fact]
    public void The_default_limits_provider_is_the_filtered_one_when_exactly_one_is_selected()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id is "claude" or "codex" ? [Weekly(50, reset)] : []);

        viewModel.SetProviderCommand.Execute("codex");
        Assert.Equal("codex", viewModel.SelectedLimitProvider);
        Assert.Equal("codex", Assert.Single(viewModel.ShownLimitGroups).ProviderId);

        // Two selected providers: the first one with rows.
        viewModel.ToggleProviderCommand.Execute("claude");
        Assert.Equal("claude", viewModel.SelectedLimitProvider);

        viewModel.SetProviderCommand.Execute("");
        Assert.Equal("claude", viewModel.SelectedLimitProvider);
    }

    [Fact]
    public void A_picked_limits_provider_is_kept_until_it_loses_its_rows()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var codexHasRows = true;
        var viewModel = Create((id, _, _) => id == "claude" || (id == "codex" && codexHasRows) ? [Weekly(50, reset)] : []);

        viewModel.LimitButtons.First(button => button.ProviderId == "codex").IsChecked = true;
        Assert.Equal("codex", viewModel.SelectedLimitProvider);
        Assert.Equal("codex", Assert.Single(viewModel.LimitButtons, button => button.IsChecked).ProviderId);
        Assert.Equal("codex", Assert.Single(viewModel.ShownLimitGroups).ProviderId);

        // A new calculation leaves the pick alone.
        viewModel.Recompute();
        Assert.Equal("codex", viewModel.SelectedLimitProvider);

        codexHasRows = false;
        viewModel.Recompute();
        Assert.Equal(["claude"], viewModel.LimitButtons.Select(button => button.ProviderId));
        Assert.True(viewModel.HasSingleLimitProvider);
        Assert.False(viewModel.HasLimitChoice);
        Assert.Equal("claude", viewModel.SelectedLimitProvider);

        // The pick does not come back when the rows do.
        codexHasRows = true;
        viewModel.Recompute();
        Assert.Equal("claude", viewModel.SelectedLimitProvider);
    }

    [Fact]
    public void Without_limit_rows_there_is_no_provider_to_pick()
    {
        var viewModel = Create((_, _, _) => []);

        Assert.Empty(viewModel.LimitButtons);
        Assert.False(viewModel.HasLimitChoice);
        Assert.False(viewModel.HasSingleLimitProvider);
        Assert.Empty(viewModel.ShownLimitGroups);
        Assert.Equal("", viewModel.SelectedLimitProvider);
        Assert.False(viewModel.HasLimitRows);
    }

    [Fact]
    public void The_range_bounds_are_passed_to_the_history_read()
    {
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        Create((_, start, end) =>
        {
            // The weekly look-back is read too, up to now; the range itself reaches the end of its last day.
            if (to is null || end > to)
            {
                from = start;
                to = end;
            }

            return [];
        });

        Assert.NotNull(from);
        Assert.True(to > from);
        Assert.True(to >= DateTimeOffset.Now);
    }

}

/// <summary>No quota history file is read on the window's own thread: the limits section and the
/// tokens-per-percent estimate get their points from the background part of a rebuild.</summary>
public class StatsViewModelHistoryThreadTests
{
    /// <summary>Runs <paramref name="body"/> on a thread of its own with a synchronization context, the
    /// way the window's thread keeps every continuation after an await.</summary>
    private static void RunOnWindowThread(Func<int, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var queue = new System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)>();
            var context = new PumpContext(queue);
            SynchronizationContext.SetSynchronizationContext(context);
            var id = Environment.CurrentManagedThreadId;
            var task = body(id);
            task.ContinueWith(_ => queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in queue.GetConsumingEnumerable())
                callback(state);
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class PumpContext(System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // the pump has finished; nothing is waiting for this callback any more
            }
        }
    }

    private static StatsViewModel CreateWithWeeklyHistory(DisposableTestDirectory directory, List<int> readThreads)
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var store = new StatsStore(directory);
        store.AddDelta([new StatsRecord("claude", DateOnly.FromDateTime(DateTime.Now), "modelA", "projA", 100, 0, 0, 0, 1)]);
        return new StatsViewModel(store)
        {
            LoadHistory = (id, _, _) =>
            {
                lock (readThreads)
                    readThreads.Add(Environment.CurrentManagedThreadId);
                return id == "claude" ? [new HistoryPoint(1, DateTimeOffset.Now.AddHours(-3), WindowKind.Weekly, 50, reset)] : [];
            },
        };
    }

    [Fact]
    public void A_rebuild_after_a_read_takes_its_quota_history_off_the_window_thread()
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-limits-thread");
        var readThreads = new List<int>();
        RunOnWindowThread(async windowThread =>
        {
            var viewModel = CreateWithWeeklyHistory(directory, readThreads);

            await viewModel.RecomputeAsync();

            Assert.NotEmpty(readThreads);
            Assert.DoesNotContain(windowThread, readThreads);
            Assert.Single(viewModel.LimitGroups);
        });
    }

    [Fact]
    public void Picking_another_range_takes_its_quota_history_off_the_window_thread_too()
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-limits-thread-range");
        var readThreads = new List<int>();
        RunOnWindowThread(async windowThread =>
        {
            var viewModel = CreateWithWeeklyHistory(directory, readThreads);
            await viewModel.RecomputeAsync();
            lock (readThreads)
                readThreads.Clear();

            await viewModel.SetRangeCommand.ExecuteAsync("All");

            Assert.NotEmpty(readThreads);
            Assert.DoesNotContain(windowThread, readThreads);
            Assert.Single(viewModel.LimitGroups);
        });
    }
}

[Collection(SharedStateTestsCollection.Name)]
public class StatsLimitTextTests
{
    private static StatsViewModel Create(IReadOnlyList<HistoryPoint> claude)
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-limits-text");
        var viewModel = new StatsViewModel(new StatsStore(directory)) { LoadHistory = (id, _, _) => id == "claude" ? claude : [] };
        viewModel.Recompute();
        return viewModel;
    }

    private static string Date(DateTimeOffset moment) =>
        DateLabels.WeekdayDayMonthShort(DateOnly.FromDateTime(moment.LocalDateTime), LocalizationService.Instance.DisplayCulture);

    private static HistoryPoint WeeklyAt(double percent, DateTimeOffset reset, int minutes) =>
        new(1, DateTimeOffset.Now.AddHours(-3).AddMinutes(minutes), WindowKind.Weekly, percent, reset);

    [Fact]
    public void A_used_limit_reset_gets_its_own_line_with_the_count_and_the_last_date()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var reset = DateTimeOffset.Now.AddDays(2);
            var viewModel = Create([WeeklyAt(100, reset, 0), WeeklyAt(4, reset, 5), WeeklyAt(90, reset, 60), WeeklyAt(3, reset, 65)]);

            var row = Assert.Single(Assert.Single(viewModel.LimitGroups).Rows);
            Assert.Equal($"2 limit resets used, last {Date(DateTimeOffset.Now.AddHours(-3).AddMinutes(65))}", row.ResetUsedText);
            Assert.True(row.HasResetUsed);
            Assert.Contains(row.ResetUsedText, row.AutomationText, StringComparison.Ordinal);
            Assert.Equal("A limit reset clears the window before its time; the statistics start a new period there.", row.ResetUsedTip);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void One_used_limit_reset_uses_the_singular_and_none_leaves_the_line_empty()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var reset = DateTimeOffset.Now.AddDays(2);
            var one = Create([WeeklyAt(100, reset, 0), WeeklyAt(4, reset, 5)]);
            var none = Create([WeeklyAt(40, reset, 0), WeeklyAt(70, reset, 5)]);

            Assert.StartsWith("1 limit reset used, last ", Assert.Single(Assert.Single(one.LimitGroups).Rows).ResetUsedText, StringComparison.Ordinal);
            var plain = Assert.Single(Assert.Single(none.LimitGroups).Rows);
            Assert.Equal("", plain.ResetUsedText);
            Assert.False(plain.HasResetUsed);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void A_row_says_how_many_cycles_hit_the_limit_when_last_and_for_how_long()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var firstReset = DateTimeOffset.Now.AddDays(-10);
            var secondReset = DateTimeOffset.Now.AddDays(2);
            var lastHit = firstReset.AddMinutes(-30);
            var latest = DateTimeOffset.Now.AddHours(-3);
            var viewModel = Create(
            [
                new HistoryPoint(1, firstReset.AddMinutes(-90), WindowKind.Weekly, 100, firstReset),
                new HistoryPoint(1, lastHit, WindowKind.Weekly, 100, firstReset),
                new HistoryPoint(1, latest, WindowKind.Weekly, 40, secondReset),
            ]);

            var group = Assert.Single(viewModel.LimitGroups);
            var row = Assert.Single(group.Rows);
            Assert.Equal($"1 of 2 cycles hit the limit ({StatusTextMap.FormatPercent("50")})", row.ReachedText);
            Assert.Equal(
                $"Avg peak {StatusTextMap.FormatPercent("70")} · highest {StatusTextMap.FormatPercent("100")}", row.DetailText);
            Assert.Equal($"last at the limit {Date(lastHit)} · blocked about {DurationFormatter.Describe(TimeSpan.FromMinutes(90))}", row.LastReachedText);
            Assert.Equal($"Readings {Date(firstReset.AddMinutes(-90))} to {Date(latest)}", group.PeriodText);
            Assert.Equal(2, row.PeriodCount);
            Assert.Equal("Crit", row.Level);
            Assert.Contains(row.LastReachedText, row.AutomationText, StringComparison.Ordinal);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void A_row_that_never_hit_the_limit_names_the_cycles_and_has_no_last_reached_line()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var viewModel = Create([new HistoryPoint(1, DateTimeOffset.Now.AddHours(-3), WindowKind.Weekly, 40, DateTimeOffset.Now.AddDays(2))]);

            var row = Assert.Single(Assert.Single(viewModel.LimitGroups).Rows);
            Assert.Equal("Never at the limit in 1 cycles", row.ReachedText);
            Assert.Equal("", row.LastReachedText);
            Assert.False(row.HasLastReached);
            Assert.Equal("Ok", row.Level);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }
}
