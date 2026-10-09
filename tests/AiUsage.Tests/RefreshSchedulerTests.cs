using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class RefreshSchedulerTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_SnapshotReady_subscriber_that_throws_is_logged_and_the_next_subscriber_still_runs()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var logged = new List<string>();
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock, log: logged.Add);
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += _ => throw new InvalidOperationException("boom in subscriber");
        scheduler.SnapshotReady += reported.Add;
        provider.NextResult = Ok(provider.Id);

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        var line = Assert.Single(logged);
        Assert.Contains(nameof(InvalidOperationException), line);
    }

    [Fact]
    public async Task A_FetchStarted_subscriber_that_throws_is_logged_and_the_fetch_still_completes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var logged = new List<string>();
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock, log: logged.Add);
        var reported = new List<ProviderSnapshot>();
        scheduler.FetchStarted += _ => throw new InvalidOperationException("boom in subscriber");
        scheduler.SnapshotReady += reported.Add;
        provider.NextResult = Ok(provider.Id);

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        Assert.Single(logged);
    }

    [Fact]
    public async Task A_failure_doubles_the_interval_a_second_failure_doubles_it_again_a_success_resets_it()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        provider.NextResult = Failed(provider.Id);
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(2));
        provider.NextResult = Failed(provider.Id);
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(4));
        provider.NextResult = Ok(provider.Id);
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(3, reported.Count);
        Assert.Equal(ProviderStatus.Ok, reported[^1].Status);

        // After the success the interval is back to normal: due again only once the base interval passed.
        clock.Advance(BaseInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(1));
        var due = scheduler.Tick();
        _ = Assert.Single(due); // discard: Assert.Single<Task> returns the Task, not just a bool
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_failed_providers_interval_doubles_up_to_a_ten_minute_cap()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        // 60s base -> 2min -> 4min -> 8min -> 10min (capped, would otherwise be 16min) -> stays 10min.
        int[] expectedIntervalMinutes = [2, 4, 8, 10, 10];
        foreach (var minutes in expectedIntervalMinutes)
        {
            provider.NextResult = Failed(provider.Id);
            await Task.WhenAll(scheduler.Tick()); // due now - verified by the previous iteration's checks below

            clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Empty(scheduler.Tick()); // not due yet at this new, doubled interval
            clock.Advance(TimeSpan.FromSeconds(1)); // now exactly due, feeding the next loop iteration
        }
    }

    // Settings.SignOut must stop every read for the disconnected account, not only
    // its web session - the scheduler is the one place every provider's fetch funnels through, so
    // this is where the flag has to actually take effect for every provider kind at once.
    [Fact]
    public async Task A_disconnected_account_is_never_fetched()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        await Task.WhenAll(scheduler.Tick(isDisconnected: _ => true));

        Assert.Equal(0, provider.FetchCount);
        var snapshot = Assert.Single(reported);
        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);

        // Still reschedules at the normal interval instead of staying due forever - a reconnect
        // (Settings' sign-in) must not have to wait out a stuck backoff.
        clock.Advance(BaseInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick(isDisconnected: _ => true));
        clock.Advance(TimeSpan.FromSeconds(1));
        var due = scheduler.Tick(isDisconnected: _ => false);
        _ = Assert.Single(due); // discard: Assert.Single<Task> returns the Task, not just a bool
        await Task.WhenAll(due);
    }

    // The reconnect itself: once isDisconnected answers false again, the very next fetch actually
    // runs - proves the flag is read fresh every time rather than only once.
    [Fact]
    public async Task Reconnecting_resumes_the_ordinary_fetch()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        await Task.WhenAll(scheduler.Tick(isDisconnected: _ => true));
        Assert.Equal(0, provider.FetchCount);

        await Task.WhenAll(scheduler.RefreshNow(isDisconnected: _ => false));
        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task A_provider_already_in_flight_is_never_started_a_second_time_by_a_concurrent_tick()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var gate = new TaskCompletionSource();
        var callCount = 0;
        var provider = new FakeProvider("codex", async ct =>
        {
            Interlocked.Increment(ref callCount);
            await gate.Task;
            return Ok("codex");
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        var firstTick = scheduler.Tick();
        var secondTick = scheduler.Tick(); // provider is now in flight - must be skipped, not started again.
        gate.SetResult();
        await Task.WhenAll(firstTick.Concat(secondTick));

        Assert.Equal(1, callCount);
        Assert.Empty(secondTick);
    }

    [Fact]
    public async Task Two_callers_racing_on_separate_threads_at_the_same_instant_still_start_a_due_provider_only_once()
    {
        // The sequential-call test above only proves the second caller sees InFlight once the first
        // caller has already returned - it cannot see the actual race the check-then-set used to
        // have. A Barrier forces both callers to reach Tick() at the same instant instead.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var gate = new TaskCompletionSource();
        var callCount = 0;
        var provider = new FakeProvider("codex", async ct =>
        {
            Interlocked.Increment(ref callCount);
            await gate.Task;
            return Ok("codex");
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        var barrier = new Barrier(2);
        IReadOnlyList<Task>? first = null;
        IReadOnlyList<Task>? second = null;

        var caller1 = Task.Run(() => { barrier.SignalAndWait(); first = scheduler.Tick(); });
        var caller2 = Task.Run(() => { barrier.SignalAndWait(); second = scheduler.Tick(); });
        await Task.WhenAll(caller1, caller2);
        gate.SetResult();
        await Task.WhenAll(first!.Concat(second!));

        Assert.Equal(1, callCount);
        Assert.Equal(1, first!.Count + second!.Count);
    }

    [Fact]
    public async Task Two_hundred_ticks_from_a_background_thread_against_four_providers_completing_on_pool_threads_never_throw()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var providers = Enumerable.Range(0, 4)
            .Select(i => new FakeProvider($"p{i}", async ct => { await Task.Yield(); return Ok($"p{i}"); }))
            .ToArray();
        var scheduler = new RefreshScheduler(providers, BaseInterval, clock);
        var started = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var caughtExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tickThread = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    foreach (var task in scheduler.Tick())
                        started.Add(task);
                }
                catch (Exception ex)
                {
                    caughtExceptions.Add(ex);
                }
            }
        });

        await tickThread;
        await Task.WhenAll(started);

        Assert.Empty(caughtExceptions);

        // Every fetch this loop could ever start has now completed - jump the clock well past any
        // provider's interval. A state dictionary corrupted by the race would either leave a
        // provider stuck InFlight (never due again) or have thrown already above.
        clock.Advance(TimeSpan.FromHours(1));
        var finalDue = scheduler.Tick();
        Assert.Equal(providers.Length, finalDue.Count);
        await Task.WhenAll(finalDue);
    }

    [Fact]
    public async Task A_provider_whose_fetch_never_completes_yields_a_failed_snapshot_after_the_timeout_and_is_due_again_after_it()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        // Never resolves on its own, but does honour the token it is handed - exactly what the
        // injected CancellationTokenSource timeout is supposed to fire against.
        var provider = new FakeProvider("codex", async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok("codex"); // unreachable
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromMilliseconds(20), maxFetchTimeout: TimeSpan.FromMilliseconds(50));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        Assert.Equal(ProviderStatus.Failed, reported[0].Status);

        // Due again once its (now doubled) backoff interval has passed - not stuck InFlight forever.
        clock.Advance(BaseInterval * 2);
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_provider_that_ignores_its_cancellation_token_and_never_completes_still_yields_a_failed_snapshot_and_is_due_again()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        // Never resolves AND never even looks at the token it is handed - the scheduler's own
        // caller-side timeout, not the provider's cooperation, must be what frees this provider up.
        var neverCompletes = new TaskCompletionSource<ProviderSnapshot>();
        var provider = new FakeProvider("codex", _ => neverCompletes.Task);
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromMilliseconds(20), maxFetchTimeout: TimeSpan.FromMilliseconds(50));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        Assert.Equal(ProviderStatus.Failed, reported[0].Status);

        // Due again once its (now doubled) backoff interval has passed - not stuck InFlight forever.
        clock.Advance(BaseInterval * 2);
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task With_a_fifteen_minute_base_interval_a_failed_providers_next_due_time_is_fifteen_minutes_not_ten()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var fifteenMinutes = TimeSpan.FromMinutes(15);
        var scheduler = new RefreshScheduler([provider], fifteenMinutes, clock);

        provider.NextResult = Failed(provider.Id);
        await Task.WhenAll(scheduler.Tick());

        clock.Advance(fifteenMinutes - TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick()); // the old 10-minute cap would already have made this due
        clock.Advance(TimeSpan.FromSeconds(1));
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_fetch_lasting_longer_than_its_interval_is_not_immediately_due_again_on_completion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider("codex", async ct => { await gate.Task; return Ok("codex"); });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        var started = scheduler.Tick();
        // The fetch takes far longer than BaseInterval to complete - NextDueAt must be measured from
        // completion, not from when the attempt started.
        clock.Advance(BaseInterval * 10);
        gate.SetResult();
        await Task.WhenAll(started);

        Assert.Empty(scheduler.Tick()); // not due the instant it completed
        clock.Advance(BaseInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(1));
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_hanging_provider_never_delays_another_providers_result()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var hangGate = new TaskCompletionSource();
        var slow = new FakeProvider("claude", async ct => { await hangGate.Task; return Ok("claude"); });
        var fast = new FakeProvider("codex", _ => Task.FromResult(Ok("codex")));
        var scheduler = new RefreshScheduler([slow, fast], BaseInterval, clock);
        var reported = new List<string>();
        scheduler.SnapshotReady += s => reported.Add(s.ProviderId);

        var started = scheduler.Tick();
        await Task.Delay(20); // let the fast provider's task complete while the slow one still hangs
        Assert.Contains("codex", reported);
        Assert.DoesNotContain("claude", reported);

        hangGate.SetResult();
        await Task.WhenAll(started);
        Assert.Contains("claude", reported);
    }

    [Fact]
    public async Task CancelProvider_cancels_the_in_flight_fetch_and_waits_for_it_to_actually_finish()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("claude", async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // honours the token it is handed
            return Ok("claude"); // unreachable
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromMilliseconds(20), maxFetchTimeout: TimeSpan.FromMilliseconds(50));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        var started = scheduler.Tick();
        await scheduler.CancelProvider("claude"); // must not return before RunOneAsync itself is done

        // The owner stopped it on purpose: no Failed snapshot and no backoff.
        Assert.Empty(reported);
        await Task.WhenAll(started); // already finished by the time CancelProvider returned above

        // Not stuck InFlight because of the cancellation - due again after the plain interval.
        clock.Advance(BaseInterval);
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task An_owner_cancel_raises_FetchEnded_for_the_provider_instead_of_a_snapshot()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("claude", async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // rethrows the cancellation
            return Ok("claude");
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromSeconds(30), maxFetchTimeout: TimeSpan.FromSeconds(30));
        var ended = new List<string>();
        var reported = new List<ProviderSnapshot>();
        scheduler.FetchEnded += ended.Add;
        scheduler.SnapshotReady += reported.Add;

        var started = scheduler.Tick();
        await scheduler.CancelProvider("claude");
        await Task.WhenAll(started);

        Assert.Equal(["claude"], ended);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_provider_that_swallows_the_owner_cancel_and_returns_Failed_reports_nothing_and_keeps_its_interval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex", async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose, like a provider that turns every error into a snapshot.
            }
            return Failed("codex");
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromSeconds(30), maxFetchTimeout: TimeSpan.FromSeconds(30));
        var ended = new List<string>();
        var reported = new List<ProviderSnapshot>();
        scheduler.FetchEnded += ended.Add;
        scheduler.SnapshotReady += reported.Add;

        var started = scheduler.Tick();
        await scheduler.CancelProvider("codex");
        await Task.WhenAll(started);

        Assert.Equal(["codex"], ended);
        Assert.Empty(reported);

        // No backoff: due again after the plain interval, not the doubled one.
        clock.Advance(BaseInterval);
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await scheduler.CancelProvider("codex");
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_real_timeout_after_an_earlier_owner_cancel_still_reports_the_timeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("claude", async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok("claude");
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromMilliseconds(20), maxFetchTimeout: TimeSpan.FromMilliseconds(50));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        var first = scheduler.Tick();
        await scheduler.CancelProvider("claude");
        await Task.WhenAll(first);
        clock.Advance(BaseInterval);
        await Task.WhenAll(scheduler.Tick());

        var snapshot = Assert.Single(reported);
        Assert.Equal("State.Failed.Detail.Timeout", snapshot.Error?.DetailKey);
    }

    [Fact]
    public void CancelProvider_for_a_provider_with_nothing_in_flight_is_a_no_op()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var scheduler = new RefreshScheduler([new FakeProvider("claude")], BaseInterval, clock);

        var task = scheduler.CancelProvider("claude");

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void CancelProvider_for_an_unknown_provider_id_is_a_no_op()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var scheduler = new RefreshScheduler([new FakeProvider("claude")], BaseInterval, clock);

        var task = scheduler.CancelProvider("does-not-exist");

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RefreshNow_bypasses_the_remaining_wait_once_without_changing_the_normal_interval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick()); // first fetch, schedules the next one 60s out

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick()); // not due yet

        await Task.WhenAll(scheduler.RefreshNow());

        clock.Advance(BaseInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(scheduler.Tick()); // normal cadence resumed after the manual refresh
        clock.Advance(TimeSpan.FromSeconds(1));
        var due = scheduler.Tick();
        _ = Assert.Single(due); // discard: Assert.Single<Task> returns the Task, not just a bool
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task A_provider_with_a_higher_minimum_interval_is_not_polled_before_its_own_floor()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("claude") { MinRefreshInterval = TimeSpan.FromMinutes(5) };
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());

        clock.Advance(BaseInterval); // past the 60s base interval, still short of the 5min floor
        Assert.Empty(scheduler.Tick());

        clock.Advance(TimeSpan.FromMinutes(5) - BaseInterval);
        var due = scheduler.Tick();
        _ = Assert.Single(due); // discard: Assert.Single<Task> returns the Task, not just a bool
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task An_exception_escaping_a_provider_is_reported_as_a_failure_instead_of_crashing_the_scheduler()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new FakeProvider("codex", _ => throw new InvalidOperationException("boom"));
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        await Task.WhenAll(scheduler.Tick());

        Assert.Single(reported);
        Assert.Equal(ProviderStatus.Failed, reported[0].Status);
    }

    [Fact]
    public async Task RefreshNow_for_one_provider_id_starts_only_that_provider_and_leaves_the_others_schedule_untouched()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var codex = new FakeProvider("codex");
        var claude = new FakeProvider("claude");
        var scheduler = new RefreshScheduler([codex, claude], BaseInterval, clock);
        var reported = new List<string>();
        scheduler.SnapshotReady += s => reported.Add(s.ProviderId);

        await Task.WhenAll(scheduler.Tick()); // both fetched once; both now due again in exactly BaseInterval
        reported.Clear();
        clock.Advance(TimeSpan.FromSeconds(30));

        var due = scheduler.RefreshNow("codex");

        _ = Assert.Single(due); // discard: Assert.Single<Task> returns the Task, not just a bool
        await Task.WhenAll(due);
        Assert.Equal(["codex"], reported);

        // claude's own NextDueAt is untouched by codex's manual refresh: still not due until the full
        // base interval has passed from the ORIGINAL tick, not from this refresh 30s into it.
        clock.Advance(BaseInterval - TimeSpan.FromSeconds(31));
        Assert.Empty(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(1));
        var dueAtOriginalMark = scheduler.Tick();
        _ = Assert.Single(dueAtOriginalMark);
        await Task.WhenAll(dueAtOriginalMark);
        Assert.Contains("claude", reported);
    }

    [Fact]
    public void RefreshNow_for_an_unknown_provider_id_is_a_no_op()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var scheduler = new RefreshScheduler([new FakeProvider("codex")], BaseInterval, clock);

        var due = scheduler.RefreshNow("does-not-exist");

        Assert.Empty(due);
    }

    [Fact]
    public void ResolveInterval_returns_the_base_interval_when_nothing_else_applies()
    {
        var result = RefreshScheduler.ResolveInterval(BaseInterval, providerFloor: null, isHidden: false, windowVisible: true);

        Assert.Equal(BaseInterval, result);
    }

    [Fact]
    public void ResolveInterval_raises_to_the_providers_own_floor_when_higher_than_the_base()
    {
        var result = RefreshScheduler.ResolveInterval(BaseInterval, TimeSpan.FromMinutes(5), isHidden: false, windowVisible: true);

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void ResolveInterval_raises_to_five_minutes_once_the_window_itself_is_not_visible()
    {
        var result = RefreshScheduler.ResolveInterval(BaseInterval, providerFloor: null, isHidden: false, windowVisible: false);

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void ResolveInterval_raises_to_fifteen_minutes_once_the_providers_own_tile_is_hidden()
    {
        var result = RefreshScheduler.ResolveInterval(BaseInterval, providerFloor: null, isHidden: true, windowVisible: true);

        Assert.Equal(TimeSpan.FromMinutes(15), result);
    }

    [Fact]
    public void ResolveInterval_combines_a_five_minute_floor_hidden_and_closed_window_into_fifteen_minutes_not_twentyfive()
    {
        var result = RefreshScheduler.ResolveInterval(BaseInterval, TimeSpan.FromMinutes(5), isHidden: true, windowVisible: false);

        Assert.Equal(TimeSpan.FromMinutes(15), result);
    }

    [Fact]
    public async Task A_provider_that_does_not_run_on_the_ui_thread_has_its_fetch_started_off_the_calling_thread()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var callingThreadId = Environment.CurrentManagedThreadId;
        var fetchThreadId = -1;
        var provider = new FakeProvider("codex", ct =>
        {
            fetchThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(Ok("codex"));
        });
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        await Task.WhenAll(scheduler.Tick());

        Assert.NotEqual(-1, fetchThreadId);
        Assert.NotEqual(callingThreadId, fetchThreadId);
    }

    [Fact]
    public async Task A_provider_flagged_to_run_on_the_ui_thread_has_its_fetch_started_on_the_calling_thread_instead()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var callingThreadId = Environment.CurrentManagedThreadId;
        var fetchThreadId = -1;
        var provider = new FakeProvider("claude", ct =>
        {
            fetchThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(Ok("claude"));
        })
        { RunsOnUiThread = true };
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);

        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(callingThreadId, fetchThreadId);
    }

    private static async Task<ProviderSnapshot> TickOnceWith(Func<CancellationToken, Task<ProviderSnapshot>> fetch)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var scheduler = new RefreshScheduler([new FakeProvider("codex", fetch)], BaseInterval, clock,
            minFetchTimeout: TimeSpan.FromMilliseconds(20), maxFetchTimeout: TimeSpan.FromMilliseconds(50));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;
        await Task.WhenAll(scheduler.Tick());
        return Assert.Single(reported);
    }

    private static string ReasonShownFor(ProviderSnapshot snapshot)
    {
        var tile = new AiUsage.ViewModels.ProviderTileViewModel("codex", "Codex");
        tile.Apply(snapshot, DateTimeOffset.UtcNow);
        return tile.ReasonText;
    }

    [Fact]
    public async Task FailedReasonNamesATimeout()
    {
        var snapshot = await TickOnceWith(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok("codex");
        });

        Assert.Equal("State.Failed.Detail.Timeout", snapshot.Error?.DetailKey);
        Assert.Equal("Action_Retry", snapshot.Error?.ActionKey);
        var loc = LocalizationService.Instance;
        Assert.Equal(loc["State.Failed.Timeout.Reason"], ReasonShownFor(snapshot));
        Assert.Equal(FailureKind.Timeout, snapshot.Error?.Kind);
    }

    [Fact]
    public async Task FailedReasonNamesTheHttpStatus()
    {
        var snapshot = await TickOnceWith(_ => throw new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway));

        Assert.Equal("State.Failed.Detail.Http", snapshot.Error?.DetailKey);
        Assert.Equal("502", snapshot.Error?.DetailArg);
        var loc = LocalizationService.Instance;
        Assert.Equal(loc.Format("State.Failed.Server.Reason", "Codex", 502), ReasonShownFor(snapshot));
        Assert.Equal(FailureKind.ServerError, snapshot.Error?.Kind);
        Assert.Equal(502, snapshot.Error?.HttpStatus);

        var noStatus = await TickOnceWith(_ => throw new System.Net.Http.HttpRequestException("offline"));
        Assert.Equal("State.Failed.Detail.Network", noStatus.Error?.DetailKey);
        Assert.Equal(FailureKind.Network, noStatus.Error?.Kind);

        var refused = await TickOnceWith(_ => throw new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound));
        Assert.Equal(FailureKind.Refused, refused.Error?.Kind);
    }

    [Fact]
    public async Task FailedReasonWithoutCauseStaysGeneric()
    {
        var snapshot = await TickOnceWith(_ => throw new InvalidOperationException("boom"));

        Assert.Null(snapshot.Error?.DetailKey);
        Assert.Equal("Action_Retry", snapshot.Error?.ActionKey);
        Assert.Equal(LocalizationService.Instance["State.Failed.Reason"], ReasonShownFor(snapshot));
    }

    private static ProviderSnapshot Ok(string providerId) =>
        new(providerId, [], "plus", SourceKind.LocalFile, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderStatus.Ok, null);

    private static ProviderSnapshot Failed(string providerId) =>
        new(providerId, [], null, SourceKind.None, DateTimeOffset.UtcNow, null, ProviderStatus.Failed, new ProviderError("Status_Failed_Reason"));

    private sealed class FakeProvider : IUsageProvider
    {
        private readonly Func<CancellationToken, Task<ProviderSnapshot>>? _fetch;

        public FakeProvider(string id) => Id = id;

        public FakeProvider(string id, Func<CancellationToken, Task<ProviderSnapshot>> fetch)
        {
            Id = id;
            _fetch = fetch;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public TimeSpan? MinRefreshInterval { get; init; }
        public bool RunsOnUiThread { get; init; }
        public ProviderSnapshot? NextResult { get; set; }

        /// <summary>How many times <see cref="FetchAsync"/> actually ran - a disconnected account
        /// must never move this off zero (see <c>A_disconnected_account_is_never_fetched</c>).</summary>
        public int FetchCount { get; private set; }

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            FetchCount++;
            return _fetch is not null ? _fetch(ct) : Task.FromResult(NextResult ?? Ok(Id));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
