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
        // Waited on through IndexCompleted rather than a signal set inside the delegate itself: that
        // would race the restartability check below against the thread still unwinding from the
        // delegate's own return, since IndexCompleted only fires once the walk is fully marked not
        // running any more.
        service.IndexCompleted += (_, _) => completed.Set();

        service.StartInBackground();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        completed.Reset();

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

        service.StartInBackground();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, completedCount);
    }

    private static StatsIndexResult Walk(int claudeRead, int codexRead) =>
        new(new StatsIndexCounters(10, claudeRead, 0, 0, 0), new StatsIndexCounters(10, codexRead, 0, 0, 0));

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
}
