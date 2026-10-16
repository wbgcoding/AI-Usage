using AiUsage.Models;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Quota history is read away from the window thread for every kind of rebuild, and a slower, older
/// load never replaces what a newer pick or a newer index snapshot has put on screen.
/// </summary>
public class StatsRebuildOrderTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.Now.AddDays(2);

    // The "All" range here spans about 200 days; a week or a month is far shorter and the weekly
    // look-back is shorter than the threshold too.
    private static bool IsLongRange(DateTimeOffset start, DateTimeOffset end) => (end - start).TotalDays > 100;

    private sealed class Rig : IDisposable
    {
        private readonly DisposableTestDirectory _directory = TestPaths.CreateDisposableDirectory("stats-rebuild-order");
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly List<(int Thread, bool Long)> Reads = [];
        public int BlockedLongReads;
        public readonly StatsViewModel ViewModel;

        public Rig()
        {
            var store = new StatsStore(_directory);
            var today = DateOnly.FromDateTime(DateTime.Now);
            store.AddDelta([
                new StatsRecord("claude", today.AddDays(-200), "modelA", "projA", 100, 0, 0, 0, 1),
                new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0, 1),
            ]);
            ViewModel = new StatsViewModel(store)
            {
                LoadHistory = (id, start, end) =>
                {
                    var isLong = IsLongRange(start, end);
                    lock (Reads)
                        Reads.Add((Environment.CurrentManagedThreadId, isLong));
                    if (isLong)
                    {
                        Interlocked.Increment(ref BlockedLongReads);
                        Gate.Wait(TimeSpan.FromSeconds(20));
                    }

                    return id == "claude" ? [new HistoryPoint(1, DateTimeOffset.Now.AddHours(-3), WindowKind.Weekly, 50, Reset)] : [];
                },
            };
        }

        public void WaitUntilALongReadIsBlocked()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref BlockedLongReads) == 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(5);
            Assert.True(Volatile.Read(ref BlockedLongReads) > 0);
        }

        public void Dispose()
        {
            Gate.Set();
            _directory.Dispose();
        }
    }

    [Fact]
    public async Task A_range_load_that_finishes_after_a_return_to_held_inputs_does_not_replace_the_shown_view()
    {
        using var rig = new Rig();
        var viewModel = rig.ViewModel;
        await viewModel.RecomputeAsync();
        Assert.Equal("Week", viewModel.SelectedRange);

        var slow = viewModel.SetRangeCommand.ExecuteAsync("All");
        rig.WaitUntilALongReadIsBlocked();
        await viewModel.SetRangeCommand.ExecuteAsync("Week");
        var shown = viewModel.Bars;

        rig.Gate.Set();
        await slow;

        Assert.Equal("Week", viewModel.SelectedRange);
        Assert.Same(shown, viewModel.Bars);
    }

    [Fact]
    public async Task An_index_snapshot_shown_during_a_range_load_is_not_replaced_by_the_older_load()
    {
        using var rig = new Rig();
        var viewModel = rig.ViewModel;
        await viewModel.RecomputeAsync();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var slow = viewModel.SetRangeCommand.ExecuteAsync("All");
        rig.WaitUntilALongReadIsBlocked();

        viewModel.TakeSnapshot = () => new StatsDataSnapshot(
            [new StatsRecord("claude", today, "modelA", "projA", 500, 0, 0, 0, 1), new StatsRecord("codex", today, "modelB", "projB", 10, 0, 0, 0, 1)], []);
        Assert.True(viewModel.TryShowSnapshot());
        var shown = viewModel.Bars;

        rig.Gate.Set();
        await slow;
        await viewModel.LimitRefreshForTest;

        Assert.Same(shown, viewModel.Bars);
    }

    [Fact]
    public async Task A_snapshot_rebuild_reads_the_quota_history_on_another_thread_and_fills_the_limits_in()
    {
        using var rig = new Rig();
        var viewModel = rig.ViewModel;
        var today = DateOnly.FromDateTime(DateTime.Now);
        viewModel.TakeSnapshot = () => new StatsDataSnapshot([new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0, 1)], []);
        var windowThread = Environment.CurrentManagedThreadId;

        Assert.True(viewModel.TryShowSnapshot());

        lock (rig.Reads)
            Assert.DoesNotContain(rig.Reads, read => read.Thread == windowThread);
        Assert.NotEmpty(viewModel.Bars);

        await viewModel.LimitRefreshForTest;

        Assert.Single(viewModel.LimitGroups);
        lock (rig.Reads)
        {
            Assert.NotEmpty(rig.Reads);
            Assert.DoesNotContain(rig.Reads, read => read.Thread == windowThread);
        }
    }

    [Fact]
    public async Task Held_inputs_older_than_the_allowed_age_are_read_again_off_the_window_thread()
    {
        using var rig = new Rig();
        var viewModel = rig.ViewModel;
        await viewModel.RecomputeAsync();
        viewModel.HeldInputsMaxAge = TimeSpan.Zero;
        int before;
        lock (rig.Reads)
            before = rig.Reads.Count;

        var slow = viewModel.SetRangeCommand.ExecuteAsync("All");
        rig.WaitUntilALongReadIsBlocked();
        await viewModel.SetRangeCommand.ExecuteAsync("Week");

        lock (rig.Reads)
            Assert.Contains(rig.Reads.Skip(before), read => !read.Long);

        rig.Gate.Set();
        await slow;
    }
}
