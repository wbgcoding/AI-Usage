using System.Text.RegularExpressions;
using AiUsage.Services;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

public class WebSessionScriptRunnerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    // The hidden browser is never disposed anywhere but DisposeBrowser (see the crash this guards
    // against, reported after a resume from hibernate: an undisposed WebView2 collected by the GC
    // has its HwndHost finalizer throw on the finalizer thread). This scans the source as text -
    // the same technique MainWindowCloseGuardTests uses - because WebView2HiddenBrowserHost cannot
    // be unit-instantiated (a live WPF Window/WebView2).
    [Fact]
    public void The_hidden_browser_is_only_ever_cleared_inside_DisposeBrowser()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Web", "WebSessionScriptRunner.cs"));

        var assignments = Regex.Matches(source, @"_webView\s*=\s*null");
        Assert.Single(assignments);

        var methodStart = source.IndexOf("private void DisposeBrowser()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "DisposeBrowser() not found");
        Assert.True(assignments[0].Index > methodStart, "_webView = null must be inside DisposeBrowser");

        var disposeAsyncStart = source.IndexOf("public ValueTask DisposeAsync()", StringComparison.Ordinal);
        Assert.True(disposeAsyncStart >= 0, "DisposeAsync() not found");
        var disposeAsyncBody = source.Substring(disposeAsyncStart, methodStart - disposeAsyncStart);
        Assert.Contains("DisposeBrowser()", disposeAsyncBody, StringComparison.Ordinal);

        var navigateStart = source.IndexOf("public async Task<bool> NavigateAsync(CancellationToken ct)", StringComparison.Ordinal);
        Assert.True(navigateStart >= 0, "NavigateAsync() not found");
        var navigateGuardEnd = source.IndexOf("var environment = await _host.EnsureEnvironmentAsync()", StringComparison.Ordinal);
        Assert.True(navigateGuardEnd > navigateStart, "NavigateAsync guard not found");
        var navigateGuardBody = source.Substring(navigateStart, navigateGuardEnd - navigateStart);
        Assert.Contains("DisposeBrowser()", navigateGuardBody, StringComparison.Ordinal);
    }

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void A_runtime_evaluate_answer_is_unwrapped_to_the_scripts_own_value()
    {
        Assert.Equal("""{"status":"ok"}""", WebView2HiddenBrowserHost.UnwrapEvaluateResult("""{"result":{"type":"object","value":{"status":"ok"}}}"""));
        Assert.Equal("", WebView2HiddenBrowserHost.UnwrapEvaluateResult("""{"result":{"type":"object"},"exceptionDetails":{"text":"x"}}"""));
        Assert.Equal("", WebView2HiddenBrowserHost.UnwrapEvaluateResult("not json"));
    }

    [Fact]
    public async Task A_navigation_that_never_completes_times_out_instead_of_hanging()
    {
        var fake = new FakeHiddenBrowserHost();
        var runner = new WebSessionScriptRunner(() => fake, navigationTimeout: ShortTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => runner.ExecuteScriptAsync("script", CancellationToken.None));
        Assert.True(fake.Disposed);
    }

    // The navigation finishes, but the runner is disposed before the start continues (a dispatcher
    // queues the continuation behind the disposal): the half-built host must not be kept.
    [Fact]
    public async Task A_runner_disposed_between_navigation_end_and_start_end_does_not_keep_the_host()
    {
        var fake = new FakeHiddenBrowserHost();
        var runner = new WebSessionScriptRunner(() => fake);
        var queue = new QueueingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<string> call;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            call = runner.ExecuteScriptAsync("script", CancellationToken.None);
            // Completed from another thread, so the continuation is queued instead of running inline.
            var completer = new Thread(() => fake.CompleteNavigate(true));
            completer.Start();
            completer.Join();
            // The disposal waits for the start, whose continuation sits in the queue: run the queue
            // before awaiting it.
            var disposing = runner.DisposeAsync().AsTask();
            queue.RunAll();
            await disposing;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(fake.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // The start is still waiting on the browser when the runner is disposed: the disposal must not
    // return before that start has torn its host down, or the caller goes on to delete the profile
    // folder while the half-built session is still closing on it.
    [Fact]
    public async Task Disposing_waits_for_a_start_that_is_still_in_flight_to_release_its_host()
    {
        var fake = new FakeHiddenBrowserHost { DisposeDelay = TimeSpan.FromMilliseconds(150) };
        var runner = new WebSessionScriptRunner(() => fake);
        var call = runner.ExecuteScriptAsync("script", CancellationToken.None);

        await runner.DisposeAsync();

        Assert.True(fake.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Shutdown_does_not_wait_for_a_start_that_is_still_in_flight()
    {
        var fake = new FakeHiddenBrowserHost();
        var runner = new WebSessionScriptRunner(() => fake);
        var queue = new QueueingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        bool finished;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            _ = runner.ExecuteScriptAsync("script", CancellationToken.None);
            var completer = new Thread(() => fake.CompleteNavigate(true));
            completer.Start();
            completer.Join();
            // The start's continuation sits in the queue, like one waiting for the UI thread that
            // shutdown blocks; the queue is never run, so a disposal that waits for it never ends.
            finished = runner.DisposeForShutdownAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(finished);
    }

    private sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
            }
        }
    }

    [Fact]
    public async Task A_script_that_never_returns_times_out_instead_of_hanging()
    {
        var fake = new FakeHiddenBrowserHost();
        fake.CompleteNavigate(true);
        var runner = new WebSessionScriptRunner(() => fake, scriptTimeout: ShortTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => runner.ExecuteScriptAsync("script", CancellationToken.None));
    }

    [Fact]
    public async Task After_a_failed_start_the_next_call_attempts_a_fresh_start()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            current = new FakeHiddenBrowserHost();
            return current;
        }, navigationTimeout: ShortTimeout);

        // First attempt: navigation never completes, so it times out and tears the host down.
        await Assert.ThrowsAsync<TimeoutException>(() => runner.ExecuteScriptAsync("script", CancellationToken.None));
        Assert.Equal(1, starts);

        // Second attempt: a fresh host that actually answers.
        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteNavigate(true);
        current.CompleteScript("result");
        var result = await secondCall;

        Assert.Equal(2, starts);
        Assert.Equal("result", result);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_wait_instead_of_hanging()
    {
        var fake = new FakeHiddenBrowserHost();
        var runner = new WebSessionScriptRunner(() => fake);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteScriptAsync("script", cts.Token));
    }

    // Both callers share one start; the first one giving up must not take it away from the second.
    [Fact]
    public async Task A_cancelled_first_caller_does_not_fail_a_second_caller_waiting_on_the_same_start()
    {
        var starts = 0;
        var fake = new FakeHiddenBrowserHost();
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            return fake;
        });
        using var firstCancel = new CancellationTokenSource();

        var first = runner.ExecuteScriptAsync("script", firstCancel.Token);
        var second = runner.ExecuteScriptAsync("script", CancellationToken.None);
        firstCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        fake.CompleteNavigate(true);
        fake.CompleteScript("answer");

        Assert.Equal("answer", await second);
        Assert.Equal(1, starts);
        Assert.False(fake.Disposed);
    }

    [Fact]
    public async Task A_navigation_that_fails_cleanly_surfaces_as_a_failure_not_a_hang()
    {
        var fake = new FakeHiddenBrowserHost();
        fake.CompleteNavigate(false);
        var runner = new WebSessionScriptRunner(() => fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteScriptAsync("script", CancellationToken.None));
    }

    [Fact]
    public async Task A_script_that_times_out_disposes_the_host_and_starts_a_fresh_one_for_the_next_call()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        }, scriptTimeout: ShortTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => runner.ExecuteScriptAsync("script", CancellationToken.None));
        var timedOutHost = current!;
        Assert.Equal(1, starts);
        Assert.True(timedOutHost.Disposed);

        // The next call must not reuse the timed-out host - a fresh one is requested instead.
        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("result");
        var result = await secondCall;

        Assert.Equal(2, starts);
        Assert.NotSame(timedOutHost, current);
        Assert.Equal("result", result);
    }

    [Fact]
    public async Task After_disposal_a_further_call_answers_not_signed_in_without_starting_a_new_host()
    {
        var starts = 0;
        var runner = new WebSessionScriptRunner(() => { starts++; return new FakeHiddenBrowserHost(); });

        await runner.DisposeAsync();
        var result = await runner.ExecuteScriptAsync("script", CancellationToken.None);

        Assert.Equal("""{"status":"not_signed_in"}""", result);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task A_host_idle_past_fifteen_minutes_is_disposed_on_the_next_call()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        }, timeProvider: clock);

        var firstCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await firstCall;
        var firstHost = current!;
        Assert.Equal(1, starts);

        clock.Advance(TimeSpan.FromMinutes(15));

        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("second");
        var result = await secondCall;

        Assert.Equal(2, starts);
        Assert.True(firstHost.Disposed);
        Assert.Equal("second", result);
    }

    [Fact]
    public async Task A_fetch_within_the_idle_span_keeps_the_same_host()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        }, timeProvider: clock);

        var firstCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await firstCall;
        var firstHost = current!;

        clock.Advance(TimeSpan.FromMinutes(14));

        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("second");
        var result = await secondCall;

        Assert.Equal(1, starts);
        Assert.False(firstHost.Disposed);
        Assert.Same(firstHost, current);
        Assert.Equal("second", result);
    }

    // A script that keeps failing is still use of the session: it must not make a healthy host look
    // idle and get rebuilt on every attempt.
    [Fact]
    public async Task A_failing_script_keeps_the_host_alive_for_the_next_call_within_the_idle_span()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runner = new WebSessionScriptRunner(() =>
        {
            starts++;
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        }, timeProvider: clock);

        var firstCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await firstCall;
        var firstHost = current!;

        clock.Advance(TimeSpan.FromMinutes(10));
        var failingCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        firstHost.FailScript(new InvalidOperationException("script failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingCall);

        clock.Advance(TimeSpan.FromMinutes(10));
        var thirdCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("third");
        var result = await thirdCall;

        Assert.Equal(1, starts);
        Assert.False(firstHost.Disposed);
        Assert.Equal("third", result);
    }

    [Fact]
    public async Task The_fetch_after_an_idle_disposal_still_answers()
    {
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runner = new WebSessionScriptRunner(() =>
        {
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        }, timeProvider: clock);

        var firstCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await firstCall;

        clock.Advance(TimeSpan.FromMinutes(20));

        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("second");
        var result = await secondCall;

        Assert.Equal("second", result);
    }

    [Fact]
    public async Task The_page_is_suspended_after_a_call_and_resumed_before_the_next_one()
    {
        FakeHiddenBrowserHost? current = null;
        var runner = new WebSessionScriptRunner(() =>
        {
            current = new FakeHiddenBrowserHost();
            current.CompleteNavigate(true);
            return current;
        });

        var firstCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await firstCall;
        Assert.Equal(1, current.SuspendCalls);
        Assert.Equal(0, current.ResumeCalls);

        var secondCall = runner.ExecuteScriptAsync("script", CancellationToken.None);
        Assert.Equal(1, current.ResumeCalls);
        current.CompleteScript("second");
        await secondCall;
        Assert.Equal(2, current.SuspendCalls);
    }

    [Fact]
    public async Task A_failing_suspend_never_fails_the_fetch()
    {
        var fake = new FakeHiddenBrowserHost { SuspendFails = true };
        fake.CompleteNavigate(true);
        var runner = new WebSessionScriptRunner(() => fake);

        var call = runner.ExecuteScriptAsync("script", CancellationToken.None);
        fake.CompleteScript("ok");

        Assert.Equal("ok", await call);
    }

    [Fact]
    public async Task An_idle_host_is_disposed_when_the_idle_span_passes_without_a_call()
    {
        var starts = 0;
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var context = new InlineSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var runner = new WebSessionScriptRunner(() =>
            {
                starts++;
                current = new FakeHiddenBrowserHost();
                current.CompleteNavigate(true);
                return current;
            }, timeProvider: clock);

            var call = runner.ExecuteScriptAsync("script", CancellationToken.None);
            current!.CompleteScript("first");
            await call;
            var first = current;

            clock.Advance(TimeSpan.FromMinutes(14));
            Assert.False(first.Disposed);

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(first.Disposed);

            var next = runner.ExecuteScriptAsync("script", CancellationToken.None);
            current!.CompleteScript("second");
            Assert.Equal("second", await next);
            Assert.Equal(2, starts);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task The_idle_timer_never_disposes_the_host_during_a_call()
    {
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var runner = new WebSessionScriptRunner(() =>
            {
                current = new FakeHiddenBrowserHost();
                current.CompleteNavigate(true);
                return current;
            }, timeProvider: clock);

            var first = runner.ExecuteScriptAsync("script", CancellationToken.None);
            current!.CompleteScript("first");
            await first;
            var host = current;

            clock.Advance(TimeSpan.FromMinutes(14));
            var second = runner.ExecuteScriptAsync("script", CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(30)); // the call is still running
            Assert.False(host.Disposed);

            host.CompleteScript("second");
            Assert.Equal("second", await second);
            Assert.False(host.Disposed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Without_a_captured_context_the_idle_timer_leaves_the_host_to_the_next_call()
    {
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var runner = new WebSessionScriptRunner(() =>
            {
                current = new FakeHiddenBrowserHost();
                current.CompleteNavigate(true);
                return current;
            }, timeProvider: clock);

            var call = runner.ExecuteScriptAsync("script", CancellationToken.None);
            current!.CompleteScript("first");
            await call;

            clock.Advance(TimeSpan.FromMinutes(20));

            Assert.False(current.Disposed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task A_resume_is_still_issued_when_the_previous_suspend_never_finished()
    {
        var fake = new FakeHiddenBrowserHost { SuspendNeverFinishes = true };
        fake.CompleteNavigate(true);
        var runner = new WebSessionScriptRunner(() => fake, suspendWait: TimeSpan.FromMilliseconds(50));

        var first = runner.ExecuteScriptAsync("script", CancellationToken.None);
        fake.CompleteScript("first");
        await first;

        var second = runner.ExecuteScriptAsync("script", CancellationToken.None);
        await Task.Delay(300);
        fake.CompleteScript("second");

        Assert.Equal("second", await second);
        Assert.Equal(1, fake.ResumeCalls);
    }

    [Fact]
    public async Task An_idle_timer_that_fires_early_is_re_armed_for_the_remaining_time()
    {
        FakeHiddenBrowserHost? current = null;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var runner = new WebSessionScriptRunner(() =>
            {
                current = new FakeHiddenBrowserHost();
                current.CompleteNavigate(true);
                return current;
            }, timeProvider: clock);

            var call = runner.ExecuteScriptAsync("script", CancellationToken.None);
            current!.CompleteScript("first");
            await call;
            var host = current;

            clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
            clock.FireAllTimers(); // a real timer can come a few milliseconds before the span is up
            Assert.False(host.Disposed);

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(host.Disposed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A hidden session that the provider itself sent to its own login page reads as "not
    /// signed in", so the tile offers a sign-in button instead of a bare failure. Only a session that
    /// ended up somewhere outside the provider's own sign-in flow still reads as blocked.</summary>
    [Theory]
    [InlineData("https://authenticate.cursor.sh/user_management/authorize", true)]
    [InlineData("https://api.workos.com/sso/authorize", true)]
    [InlineData("https://cursor.com.attacker.test/login", false)]
    [InlineData("http://cursor.com/dashboard", false)]
    [InlineData("not a url at all", false)]
    public void ASessionSentToTheProvidersOwnLoginPageReadsAsNotSignedInRatherThanBlocked(string source, bool notSignedIn)
    {
        var envelope = WebView2HiddenBrowserHost.OffOriginEnvelope(
            source, ProviderRegistry.WebSessionFor("cursor").AllowedHosts);

        Assert.Equal(notSignedIn ? """{"status":"not_signed_in"}""" : """{"status":"blocked"}""", envelope);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private readonly List<FakeTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void FireAllTimers()
        {
            foreach (var timer in _timers.ToArray())
            {
                if (timer.Disposed || timer.Fired)
                    continue;
                timer.Fired = true;
                timer.Callback(timer.State);
            }
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.Disposed || timer.Fired || timer.DueAt > _now)
                    continue;
                timer.Fired = true;
                timer.Callback(timer.State);
            }
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset DueAt { get; } = dueAt;
        public bool Fired { get; set; }
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for the UI thread's context: counts the posts and runs them inline.</summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            d(state);
        }
    }

    private sealed class FakeHiddenBrowserHost : IHiddenBrowserHost
    {
        private readonly TaskCompletionSource<bool> _navigate = new();

        // Replaced on every call so a host reused across several ExecuteScriptAsync calls (the idle
        // disposal tests keep the same fake host alive across two calls) answers each one on its own
        // completion source instead of replaying the first call's already-set result.
        private TaskCompletionSource<string> _script = new();

        public bool Disposed { get; private set; }

        public int SuspendCalls { get; private set; }

        public int ResumeCalls { get; private set; }

        public bool SuspendFails { get; init; }

        public bool SuspendNeverFinishes { get; init; }

        public Task SuspendAsync()
        {
            SuspendCalls++;
            if (SuspendNeverFinishes)
                return new TaskCompletionSource().Task;
            return SuspendFails ? Task.FromException(new InvalidOperationException("suspend failed")) : Task.CompletedTask;
        }

        public void Resume() => ResumeCalls++;

        /// <summary>How long closing the host takes, to model a browser that shuts down slowly.</summary>
        public TimeSpan DisposeDelay { get; init; }

        public void CompleteNavigate(bool success) => _navigate.TrySetResult(success);

        public void CompleteScript(string result) => _script.TrySetResult(result);

        public void FailScript(Exception error) => _script.TrySetException(error);

        public Task<bool> NavigateAsync(CancellationToken ct) => _navigate.Task;

        public Task<string> ExecuteScriptAsync(string script, CancellationToken ct)
        {
            _script = new TaskCompletionSource<string>();
            return _script.Task;
        }

        public async ValueTask DisposeAsync()
        {
            if (DisposeDelay > TimeSpan.Zero)
                await Task.Delay(DisposeDelay);
            Disposed = true;
        }
    }
}
