using System.Text.Json;
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

    [Fact]
    public async Task A_runner_disposed_while_waiting_for_the_page_to_wake_cancels_instead_of_running_on_a_dead_host()
    {
        FakeHiddenBrowserHost? current = null;
        var runner = new WebSessionScriptRunner(() =>
        {
            current = new FakeHiddenBrowserHost { SuspendNeverFinishes = true };
            current.CompleteNavigate(true);
            return current;
        }, suspendWait: TimeSpan.FromSeconds(30));

        var first = runner.ExecuteScriptAsync("script", CancellationToken.None);
        current!.CompleteScript("first");
        await first;
        var host = current;
        Assert.Equal(1, host.ExecuteCalls);

        // The second call waits for the suspend that never finishes; the runner goes away meanwhile.
        var second = runner.ExecuteScriptAsync("script", CancellationToken.None);
        await runner.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, host.ExecuteCalls);
        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task The_timeout_timer_is_cancelled_as_soon_as_the_work_wins()
    {
        CancellationToken timerToken = default;
        var work = Task.CompletedTask;

        var won = await WebSessionScriptRunner.WinsAgainstTimeoutAsync(
            work, TimeSpan.FromMinutes(5), CancellationToken.None,
            delay: (_, token) =>
            {
                timerToken = token;
                return Task.Delay(Timeout.Infinite, token);
            });

        Assert.True(won);
        Assert.True(timerToken.IsCancellationRequested);
    }

    [Fact]
    public async Task The_timeout_wins_against_work_that_never_finishes_and_so_does_the_callers_cancellation()
    {
        var never = new TaskCompletionSource().Task;
        Assert.False(await WebSessionScriptRunner.WinsAgainstTimeoutAsync(never, TimeSpan.FromMilliseconds(20), CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var cancelled = WebSessionScriptRunner.WinsAgainstTimeoutAsync(never, TimeSpan.FromMinutes(5), cts.Token);
        await cts.CancelAsync();
        Assert.False(await cancelled);
    }

    [Fact]
    public void Every_script_runs_behind_a_host_check_evaluated_in_the_same_step()
    {
        const string script = "(async () => ({status: 'ok'}))()";

        var wrapped = WebView2HiddenBrowserHost.WrapWithOriginGuard(script, "claude.ai");

        Assert.Contains("location.hostname", wrapped, StringComparison.Ordinal);
        Assert.Contains("location.protocol", wrapped, StringComparison.Ordinal);
        Assert.Contains("\"claude.ai\"", wrapped, StringComparison.Ordinal);
        Assert.Contains(WebView2HiddenBrowserHost.OffOriginStatus, wrapped, StringComparison.Ordinal);
        Assert.True(
            wrapped.IndexOf("location.hostname", StringComparison.Ordinal) < wrapped.IndexOf(script, StringComparison.Ordinal),
            "the host check has to stand in front of the script");
    }

    [Fact]
    public void The_host_check_uses_no_method_of_a_built_in_prototype()
    {
        var wrapped = WebView2HiddenBrowserHost.WrapWithOriginGuard("1", "claude.ai");
        var check = wrapped[..wrapped.IndexOf("else {", StringComparison.Ordinal)];

        foreach (var method in new[] { "endsWith", "startsWith", "indexOf", "includes", "slice", "substring", "charAt", "split", "match", "test(" })
            Assert.DoesNotContain(method, check, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https:", "claude.ai", false)]
    [InlineData("https:", "api.claude.ai", false)]
    [InlineData("https:", "sites.google.com", true)]
    [InlineData("https:", "claude.ai.evil.test", true)]
    [InlineData("https:", "evilclaude.ai", true)]
    [InlineData("http:", "claude.ai", true)]
    public void A_page_that_replaces_endsWith_cannot_get_past_the_host_check(string protocol, string hostname, bool refused)
    {
        var node = TryRunNode(WebView2HiddenBrowserHost.WrapWithOriginGuard("({status: 'ran'})", "claude.ai"), protocol, hostname);
        if (node is null)
            return; // no node on this machine: the check above still pins the shape of the guard

        Assert.True(refused == node.Contains(WebView2HiddenBrowserHost.OffOriginStatus, StringComparison.Ordinal), node);
        Assert.True(!refused == node.Contains("\"ran\"", StringComparison.Ordinal), node);
    }

    /// <summary>Evaluates the guarded script in a fresh JavaScript context whose <c>String.prototype.endsWith</c>
    /// always answers true, as a hostile page would leave it; null when node is not installed.</summary>
    private static string? TryRunNode(string wrapped, string protocol, string hostname)
    {
        const string driver =
            "const vm = require('vm'); const i = JSON.parse(require('fs').readFileSync(0, 'utf8'));" +
            "const c = vm.createContext({ location: { protocol: i.protocol, hostname: i.hostname, href: i.protocol + '//' + i.hostname + '/' } });" +
            "vm.runInContext('String.prototype.endsWith = () => true;', c);" +
            "console.log(JSON.stringify(vm.runInContext(i.wrapped, c)));";
        var start = new System.Diagnostics.ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(driver);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardInput.Write(JsonSerializer.Serialize(new { wrapped, protocol, hostname }));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20000))
            {
                process.Kill();
                return null;
            }

            Assert.True(process.ExitCode == 0, error);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void A_navigation_turned_away_is_logged_once_per_host_without_path_or_query()
    {
        IReadOnlyList<string> allowed = ["aistudio.google.com", "accounts.google.com"];
        var gate = new FirstPerHostGate();

        var first = WebView2HiddenBrowserHost.BlockedNavigationLine("https://consent.google.com/ml?continue=secret&code=1", allowed, gate);
        var again = WebView2HiddenBrowserHost.BlockedNavigationLine("https://consent.google.com/other", allowed, gate);
        var other = WebView2HiddenBrowserHost.BlockedNavigationLine("https://www.google.com/x", allowed, gate);

        Assert.NotNull(first);
        Assert.Contains("consent.google.com", first, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/ml", first, StringComparison.Ordinal);
        Assert.Null(again);
        Assert.Contains("www.google.com", other, StringComparison.Ordinal);
        Assert.Null(WebView2HiddenBrowserHost.BlockedNavigationLine("https://aistudio.google.com/usage", allowed, gate));
    }

    [Fact]
    public void A_host_name_cannot_break_out_of_the_guards_string_literal()
    {
        var wrapped = WebView2HiddenBrowserHost.WrapWithOriginGuard("1", "a\";alert(1);//");

        Assert.DoesNotContain("\";alert(1)", wrapped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":"__off_origin","href":"https://accounts.google.com/signin"}""", "https://accounts.google.com/signin")]
    [InlineData("""{"status":"ok","body":"{}"}""", null)]
    [InlineData("""{"status":"__off_origin"}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_guards_answer_is_told_apart_from_a_scripts_own(string answer, string? expectedAddress)
    {
        Assert.Equal(expectedAddress, WebView2HiddenBrowserHost.ReadOffOriginAddress(answer));
    }

    [Fact]
    public void A_guarded_script_off_the_provider_answers_exactly_what_the_host_side_check_answers()
    {
        // The guard hands the address back; the classification is the one the pre-check already uses.
        IReadOnlyList<string> allowed = ["claude.ai", "accounts.google.com"];

        Assert.Equal("""{"status":"not_signed_in"}""", WebView2HiddenBrowserHost.OffOriginEnvelope("https://accounts.google.com/x", allowed));
        Assert.Equal("""{"status":"blocked"}""", WebView2HiddenBrowserHost.OffOriginEnvelope("https://elsewhere.example/", allowed));
        Assert.Equal("""{"status":"blocked"}""", WebView2HiddenBrowserHost.OffOriginEnvelope("http://claude.ai/", allowed));
    }

    [Theory]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind.Alert, false)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind.Confirm, false)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind.Prompt, false)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind.Beforeunload, true)]
    public void Only_the_leave_page_prompt_is_answered_yes_in_the_hidden_session(
        Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind kind, bool accepted)
    {
        Assert.Equal(accepted, WebView2HiddenBrowserHost.ShouldAcceptScriptDialog(kind));
    }

    [Fact]
    public void The_page_cannot_open_the_print_dialog_in_the_hidden_session()
    {
        Assert.Contains("'print'", WebView2HiddenBrowserHost.DocumentCreatedScript, StringComparison.Ordinal);
        Assert.Contains("configurable:false", WebView2HiddenBrowserHost.DocumentCreatedScript, StringComparison.Ordinal);
    }

    // The host cannot be instantiated without a live WebView2, so its wiring is read as text (as above).
    [Fact]
    public void The_hidden_session_silences_dialogs_prompts_popups_and_foreign_navigation()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Web", "WebSessionScriptRunner.cs"));
        var start = source.IndexOf("public async Task<bool> NavigateAsync(CancellationToken ct)", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task SuspendAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var navigate = source[start..end];

        Assert.Contains("AreDefaultScriptDialogsEnabled = false;", navigate, StringComparison.Ordinal);
        Assert.Contains("ScriptDialogOpening +=", navigate, StringComparison.Ordinal);
        Assert.Contains("BasicAuthenticationRequested +=", navigate, StringComparison.Ordinal);
        Assert.Contains("ClientCertificateRequested +=", navigate, StringComparison.Ordinal);
        Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync(DocumentCreatedScript)", navigate, StringComparison.Ordinal);
        Assert.Contains("NewWindowRequested +=", navigate, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", navigate, StringComparison.Ordinal);
        Assert.Contains("DownloadStarting += (_, e) => e.Cancel = true;", navigate, StringComparison.Ordinal);
        Assert.Contains("PermissionRequested += (_, e) => e.State", navigate, StringComparison.Ordinal);
        Assert.Contains("SignInNavigationPolicy.IsAllowedUri(e.Uri, _allowedHosts)", navigate, StringComparison.Ordinal);

        // The dialogs are switched off before the first page can load.
        Assert.True(
            navigate.IndexOf("AreDefaultScriptDialogsEnabled = false;", StringComparison.Ordinal)
            < navigate.IndexOf("CoreWebView2.Navigate(_baseUrl)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hidden_host_never_dereferences_the_web_view_with_a_bang_after_disposal()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Web", "WebSessionScriptRunner.cs"));

        Assert.DoesNotContain("_webView!", source, StringComparison.Ordinal);
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

        public int ExecuteCalls { get; private set; }

        public Task<string> ExecuteScriptAsync(string script, CancellationToken ct)
        {
            ExecuteCalls++;
            // The real host reads a field that disposal has already cleared.
            if (Disposed)
                throw new NullReferenceException();
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
