using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>
/// Lifecycle coverage for <see cref="StatsIndexerService"/>: only one walk running at a time, a
/// finished walk can be started again so usage made while the app keeps running still reaches the
/// statistics, and a clean abort on dispose. Driven through the internal delegate seam so no real
/// session history is ever touched and no test has to guess at timing with a sleep.
/// </summary>
public class StatsIndexerServiceTests
{
    [Fact]
    public void StartInBackground_ignores_a_second_call_while_the_first_walk_is_still_running()
    {
        var runCount = 0;
        var started = new ManualResetEventSlim(initialState: false);
        var release = new ManualResetEventSlim(initialState: false);

        using var service = new StatsIndexerService(token =>
        {
            Interlocked.Increment(ref runCount);
            started.Set();
            release.Wait();
        });

        service.StartInBackground();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        // The walk is still running at this point - a second call must not start an overlapping one.
        service.StartInBackground();
        release.Set();

        Assert.Equal(1, runCount);
    }

    [Fact]
    public void A_walk_that_throws_neither_escapes_the_thread_nor_blocks_the_next_walk()
    {
        var runCount = 0;
        var completed = new ManualResetEventSlim(initialState: false);
        using var service = new StatsIndexerService(token =>
        {
            if (Interlocked.Increment(ref runCount) == 1)
                throw new InvalidOperationException("broken index");
        });
        service.IndexCompleted += (_, _) => completed.Set();

        service.StartInBackground();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        completed.Reset();
        WaitIdle(service);
        service.StartInBackground();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, runCount);
    }

    [Fact]
    public void A_throwing_IndexCompleted_subscriber_does_not_skip_the_others()
    {
        var reached = new ManualResetEventSlim(initialState: false);
        using var service = new StatsIndexerService(token => { });
        service.IndexCompleted += (_, _) => throw new InvalidOperationException("bad handler");
        service.IndexCompleted += (_, _) => reached.Set();

        service.StartInBackground();

        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void StartInBackground_after_a_finished_walk_starts_a_new_one()
    {
        var runCount = 0;
        var completed = new ManualResetEventSlim(initialState: false);

        using var service = new StatsIndexerService(token => Interlocked.Increment(ref runCount));
        // Waited on through IndexCompleted rather than a signal set inside the delegate itself, then
        // until the thread has marked the walk finished: the event fires just before that.
        service.IndexCompleted += (_, _) => completed.Set();

        service.StartInBackground();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        completed.Reset();
        WaitIdle(service);

        // The first walk already finished by now, so this call is a fresh walk, not a no op -
        // usage made while the app keeps running must still reach the statistics.
        service.StartInBackground();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, runCount);
    }

    [Fact]
    public void IndexCompleted_fires_once_after_each_walk_finishes()
    {
        var completedCount = 0;
        var finished = new ManualResetEventSlim(initialState: false);

        using var service = new StatsIndexerService(token => { });
        service.IndexCompleted += (_, _) =>
        {
            Interlocked.Increment(ref completedCount);
            finished.Set();
        };

        service.StartInBackground();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        finished.Reset();
        WaitIdle(service);

        service.StartInBackground();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, completedCount);
    }

    private static StatsIndexResult Walk(int claudeRead, int codexRead) =>
        new(new StatsIndexCounters(10, claudeRead, 0, 0, 0), new StatsIndexCounters(10, codexRead, 0, 0, 0));

    private static StatsDataSnapshot EmptySnapshot() => new([], []);

    private static void WaitIdle(StatsIndexerService service) =>
        Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));

    [Fact]
    public void A_start_while_the_snapshot_loads_is_ignored()
    {
        var walks = 0;
        using var loading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var service = StatsIndexerService.ForTest(_ =>
        {
            Interlocked.Increment(ref walks);
            return Walk(1, 0);
        }, loadSnapshot: () =>
        {
            loading.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return EmptySnapshot();
        });

        service.StartInBackground();
        Assert.True(loading.Wait(TimeSpan.FromSeconds(5)));
        service.StartInBackground(); // the first thread is still busy with its snapshot
        release.Set();
        WaitIdle(service);

        Assert.Equal(1, walks);
    }

    private static void RunWalk(StatsIndexerService service)
    {
        using var raised = new ManualResetEventSlim();
        void OnRaised(object? sender, EventArgs e) => raised.Set();
        service.IndexCompleted += OnRaised;
        service.StartInBackground();
        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        service.IndexCompleted -= OnRaised;
        WaitIdle(service);
    }

    [Fact]
    public void A_raising_walk_sets_Latest_and_a_walk_that_read_nothing_keeps_the_old_one()
    {
        var results = new Queue<StatsIndexResult>([Walk(1, 0), Walk(0, 0), Walk(1, 0)]);
        var loads = 0;
        using var service = StatsIndexerService.ForTest(_ => results.Dequeue(), loadSnapshot: () =>
        {
            Interlocked.Increment(ref loads);
            return EmptySnapshot();
        });
        Assert.Null(service.Latest);

        RunWalk(service);
        var first = service.Latest;
        Assert.NotNull(first);

        service.StartInBackground();
        Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));
        Thread.Sleep(150); // a quiet walk has nothing to load; give a wrongly started load time to show
        Assert.Same(first, service.Latest);
        Assert.Equal(1, loads);

        RunWalk(service);
        Assert.NotSame(first, service.Latest);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void The_snapshot_is_ready_before_IndexCompleted_is_raised()
    {
        using var service = StatsIndexerService.ForTest(_ => Walk(1, 0), loadSnapshot: EmptySnapshot);
        StatsDataSnapshot? seenByHandler = null;
        using var raised = new ManualResetEventSlim();
        service.IndexCompleted += (_, _) =>
        {
            seenByHandler = service.Latest;
            raised.Set();
        };

        service.StartInBackground();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(seenByHandler);
    }

    [Fact]
    public void RefreshSnapshotInBackground_clears_Latest_at_once_and_sets_the_new_snapshot_after()
    {
        using var release = new ManualResetEventSlim();
        var loads = 0;
        var fresh = EmptySnapshot();
        using var service = StatsIndexerService.ForTest(_ => Walk(1, 0), loadSnapshot: () =>
        {
            if (Interlocked.Increment(ref loads) == 1)
                return EmptySnapshot();
            release.Wait(TimeSpan.FromSeconds(5));
            return fresh;
        });
        RunWalk(service);
        Assert.NotNull(service.Latest);

        service.RefreshSnapshotInBackground();

        Assert.Null(service.Latest);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => service.Latest is not null, TimeSpan.FromSeconds(5)));
        Assert.Same(fresh, service.Latest);
    }

    [Fact]
    public void RefreshSnapshotInBackground_raises_IndexCompleted_once_with_the_new_snapshot_in_place()
    {
        var loads = 0;
        var fresh = EmptySnapshot();
        using var service = StatsIndexerService.ForTest(_ => Walk(1, 0), loadSnapshot: () =>
            Interlocked.Increment(ref loads) == 1 ? EmptySnapshot() : fresh);
        RunWalk(service);

        var raised = 0;
        StatsDataSnapshot? seenByHandler = null;
        using var signal = new ManualResetEventSlim();
        service.IndexCompleted += (_, _) =>
        {
            seenByHandler = service.Latest;
            Interlocked.Increment(ref raised);
            signal.Set();
        };

        service.RefreshSnapshotInBackground();

        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(150); // a second, wrongly raised event would land by now
        Assert.Equal(1, raised);
        Assert.Same(fresh, seenByHandler);
    }

    [Fact]
    public void RefreshSnapshotInBackground_raises_IndexCompleted_also_when_the_load_failed()
    {
        var loads = 0;
        using var service = StatsIndexerService.ForTest(_ => Walk(1, 0), loadSnapshot: () =>
            Interlocked.Increment(ref loads) == 1 ? EmptySnapshot() : throw new InvalidOperationException("broken index"));
        RunWalk(service);

        var raised = 0;
        using var signal = new ManualResetEventSlim();
        service.IndexCompleted += (_, _) =>
        {
            Interlocked.Increment(ref raised);
            signal.Set();
        };

        service.RefreshSnapshotInBackground();

        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(150);
        Assert.Equal(1, raised);
        Assert.Null(service.Latest); // the window reads the store itself
    }

    [Fact]
    public void A_failing_snapshot_load_keeps_the_old_snapshot_and_logs_once()
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-indexer-snapshot-log");
        var log = new AiUsage.Services.LogService(directory);
        var loads = 0;
        using var service = StatsIndexerService.ForTest(_ => Walk(1, 0), loadSnapshot: () =>
        {
            if (Interlocked.Increment(ref loads) == 1)
                return EmptySnapshot();
            throw new InvalidOperationException("broken index");
        }, logService: log);
        RunWalk(service);
        var first = service.Latest;
        Assert.NotNull(first);

        RunWalk(service);

        Assert.Same(first, service.Latest);
        var logged = File.ReadAllLines(log.CurrentFile).Count(line => line.Contains("Statistics snapshot failed"));
        Assert.Equal(1, logged);
    }

    [Fact]
    public void ShouldRaise_needs_a_file_read_the_first_completed_walk_or_a_new_day()
    {
        Assert.True(StatsIndexerService.ShouldRaise(Walk(0, 0), firstWalkDone: false, dateChanged: false));
        Assert.False(StatsIndexerService.ShouldRaise(Walk(0, 0), firstWalkDone: true, dateChanged: false));
        Assert.True(StatsIndexerService.ShouldRaise(Walk(1, 0), firstWalkDone: true, dateChanged: false));
        Assert.True(StatsIndexerService.ShouldRaise(Walk(0, 3), firstWalkDone: true, dateChanged: false));
        // Midnight passed: the day grid and an open statistics window must move their "today".
        Assert.True(StatsIndexerService.ShouldRaise(Walk(0, 0), firstWalkDone: true, dateChanged: true));
        // No result (a cancelled or failed walk) may have read files: subscribers are told.
        Assert.True(StatsIndexerService.ShouldRaise(null, firstWalkDone: true, dateChanged: false));
    }

    [Fact]
    public void A_walk_that_read_nothing_is_not_announced_after_the_first_one()
    {
        var results = new Queue<StatsIndexResult>([Walk(0, 0), Walk(0, 0), Walk(0, 0), Walk(2, 0)]);
        var raised = 0;
        var day = new DateOnly(2026, 10, 7);
        using var service = StatsIndexerService.ForTest(_ => results.Dequeue(), () => day);
        service.IndexCompleted += (_, _) => Interlocked.Increment(ref raised);

        void RunWalk()
        {
            service.StartInBackground();
            Assert.True(SpinWait.SpinUntil(() => !service.IsRunning, TimeSpan.FromSeconds(5)));
            Thread.Sleep(150); // the handler runs just after the walk is marked finished
        }

        RunWalk();
        Assert.Equal(1, raised); // the first completed walk always signals
        RunWalk();
        Assert.Equal(1, raised); // nothing read, nothing to reload
        day = day.AddDays(1);
        RunWalk();
        Assert.Equal(2, raised); // nothing read, but midnight passed
        RunWalk();
        Assert.Equal(3, raised); // a file was read
    }

    [Fact]
    public void Dispose_cancels_a_walk_that_is_still_running()
    {
        var started = new ManualResetEventSlim(initialState: false);
        var observedCancellation = new ManualResetEventSlim(initialState: false);

        var service = new StatsIndexerService(token =>
        {
            started.Set();
            while (!token.IsCancellationRequested)
                Thread.Sleep(5);
            observedCancellation.Set();
            token.ThrowIfCancellationRequested();
        });

        service.StartInBackground();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        service.Dispose();

        Assert.True(observedCancellation.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dispose_returns_within_the_bound_when_the_walk_ignores_cancellation()
    {
        var started = new ManualResetEventSlim(initialState: false);
        var stuck = new ManualResetEventSlim(initialState: false);
        var service = new StatsIndexerService(token =>
        {
            started.Set();
            stuck.Wait(); // an enumeration blocked in the file system, deaf to the token
        });

        try
        {
            service.StartInBackground();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            var dispose = Task.Run(service.Dispose);

            await dispose.WaitAsync(TimeSpan.FromSeconds(6));
        }
        finally
        {
            stuck.Set();
        }
    }

    [Fact]
    public void The_directory_walk_stops_when_the_token_is_cancelled()
    {
        using var root = TestPaths.CreateDisposableDirectory("walk-cancel");
        File.WriteAllText(Path.Combine(root, "a.jsonl"), "x");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => StatsIndexer.EnumerateNewestFirst(root, "*.jsonl", out _, cancelled.Token));
    }

    [Fact]
    public void A_finished_walk_writes_the_weekly_copy_while_it_is_enabled()
    {
        var copies = 0;
        using var service = StatsIndexerService.ForTest(
            _ => Walk(1, 0), loadSnapshot: EmptySnapshot, weeklyCopy: _ => Interlocked.Increment(ref copies));

        service.StartInBackground();
        WaitIdle(service);

        Assert.Equal(1, copies);
    }

    [Fact]
    public void No_weekly_copy_is_written_while_it_is_switched_off()
    {
        var copies = 0;
        using var service = StatsIndexerService.ForTest(
            _ => Walk(1, 0), loadSnapshot: EmptySnapshot, weeklyCopy: _ => Interlocked.Increment(ref copies));
        service.WeeklyCopyEnabled = false;

        service.StartInBackground();
        WaitIdle(service);

        Assert.Equal(0, copies);
    }

    [Fact]
    public void No_weekly_copy_is_written_after_a_cancelled_walk()
    {
        var copies = 0;
        StatsIndexerService? holder = null;
        using var service = StatsIndexerService.ForTest(_ =>
        {
            holder!.Cancel();
            return Walk(1, 0);
        }, loadSnapshot: EmptySnapshot, weeklyCopy: _ => Interlocked.Increment(ref copies));
        holder = service;

        service.StartInBackground();
        WaitIdle(service);

        Assert.Equal(0, copies);
    }

    [Fact]
    public void A_failing_weekly_copy_neither_ends_the_thread_nor_blocks_the_next_walk()
    {
        var walks = 0;
        using var service = StatsIndexerService.ForTest(_ =>
        {
            Interlocked.Increment(ref walks);
            return Walk(1, 0);
        }, loadSnapshot: EmptySnapshot, weeklyCopy: _ => throw new InvalidOperationException("disk"));

        service.StartInBackground();
        WaitIdle(service);
        service.StartInBackground();
        WaitIdle(service);

        Assert.Equal(2, walks);
    }
}
