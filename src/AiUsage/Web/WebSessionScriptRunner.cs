using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Web;

/// <summary>
/// Runs one web-backed provider's own discovery/fetch scripts (e.g.
/// Providers/ClaudeDiscoveryScript.cs for Claude) in a hidden browser session that shares the
/// sign-in window's persistent profile (see <see cref="WebViewHost"/>), so a session started there
/// is what this reads from. Created on first use and kept for the process lifetime rather than per
/// fetch: tearing down and rebuilding a WebView2 environment on every tick would be far more
/// expensive than the memory of keeping it. Both navigation and script execution race against a
/// timeout, and a failed start tears itself down completely, so a browser that never answers cannot
/// hang every later fetch forever. One instance per web-backed provider.
/// </summary>
public sealed class WebSessionScriptRunner : IAsyncDisposable
{
    private static readonly TimeSpan DefaultNavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(20);

    // A browser process resident for days is this app's largest single memory cost, and most of that
    // time nobody asked it for anything - a runner idle this long tears its host down on the next
    // call instead of keeping it warm forever, then starts fresh through the same path a timed out
    // script already uses.
    private static readonly TimeSpan IdleDisposalSpan = TimeSpan.FromMinutes(15);

    // Longest a call waits for the previous suspend before resuming anyway.
    private static readonly TimeSpan DefaultSuspendWait = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _suspendWait;

    // What the caller (WebUsageSource) reads as "not signed in" - see WebUsageSource.Interpret.
    private const string NotSignedInEnvelope = """{"status":"not_signed_in"}""";

    private readonly Func<IHiddenBrowserHost> _hostFactory;
    private readonly TimeSpan _navigationTimeout;
    private readonly TimeSpan _scriptTimeout;
    private readonly TimeProvider _timeProvider;
    private IHiddenBrowserHost? _host;
    private Task? _ready;

    // Set once EnsureReadyAsync's host is actually in hand, at the start of every ExecuteScriptAsync
    // call and again after a finished one - the idle clock this runner is measured against. A script
    // that fails still counts as use, or a healthy host would be rebuilt on every failing attempt.
    private DateTimeOffset? _lastActivityAt;

    // Calls between their start and their end. The idle timer only ever disposes a host while this is
    // zero, so a slow script is never cut off underneath itself.
    private int _inFlight;

    // The context the current host was created on (the UI thread in production). WebView2 objects
    // belong to that thread, so a timer firing on a pool thread hands the disposal back to it; with
    // none captured the timer does nothing and the next call's own idle check still applies.
    private SynchronizationContext? _hostContext;
    private ITimer? _idleTimer;

    // The page suspend that follows a finished call. The next call waits for it before resuming, so
    // a resume never races a suspend still in progress.
    private Task? _suspendTask;
    private bool _suspendFailureLogged;

    // Set once by DisposeAsync and never cleared: an owner that disposed this instance (sign-out,
    // account removal, app shutdown) still holds the delegate a provider's WebUsageSource captured
    // at construction, so a later fetch can still call ExecuteScriptAsync on a runner that is
    // supposed to be gone. Once disposed, every further call answers "not signed in" directly
    // instead of quietly starting a brand new browser session against a profile folder that may
    // already have been deleted.
    private bool _disposed;

    // Owns the shared start (_ready): one caller's own token must not cancel a start other callers
    // are waiting on. Cancelled by DisposeAsync.
    private readonly CancellationTokenSource _lifetime = new();

    public WebSessionScriptRunner(WebSessionDescriptor descriptor)
        : this(() => new WebView2HiddenBrowserHost(new WebViewHost(descriptor), descriptor))
    {
    }

    /// <summary>Test seam: a fake host instead of a real WebView2 session, shorter timeouts so a
    /// test proving the timeout path does not have to wait 30 real seconds, and an injectable clock
    /// for the idle disposal span. The factory is called once per start attempt, so a test can count
    /// how many times a failed start is retried.</summary>
    internal WebSessionScriptRunner(
        Func<IHiddenBrowserHost> hostFactory, TimeSpan? navigationTimeout = null, TimeSpan? scriptTimeout = null, TimeProvider? timeProvider = null,
        TimeSpan? suspendWait = null)
    {
        _suspendWait = suspendWait ?? DefaultSuspendWait;
        _hostFactory = hostFactory;
        _navigationTimeout = navigationTimeout ?? DefaultNavigationTimeout;
        _scriptTimeout = scriptTimeout ?? DefaultScriptTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> ExecuteScriptAsync(string script, CancellationToken ct)
    {
        if (_disposed)
            return NotSignedInEnvelope;

        ct.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _inFlight);
        DisarmIdleTimer();
        try
        {
            return await RunScriptAsync(script, ct);
        }
        finally
        {
            if (Interlocked.Decrement(ref _inFlight) == 0)
                ArmIdleTimer();
        }
    }

    private async Task<string> RunScriptAsync(string script, CancellationToken ct)
    {

        // A host that has sat unused past the idle span is torn down here, before it is asked to do
        // anything else - EnsureReadyAsync below then starts a completely fresh one, the same path a
        // timed out script already takes.
        if (_host is not null && _lastActivityAt is { } lastActivity && _timeProvider.GetUtcNow() - lastActivity >= IdleDisposalSpan)
        {
            var idleHost = _host;
            _host = null;
            _ready = null;
            await idleHost.DisposeAsync();
        }

        _lastActivityAt = _timeProvider.GetUtcNow();

        var ready = _ready ??= EnsureReadyAsync(_lifetime.Token);
        try
        {
            await ready.WaitAsync(ct);
        }
        catch
        {
            // A start that failed synchronously clears _ready inside EnsureReadyAsync before the
            // assignment above stores it again - clear it here too, or every later call replays it.
            // A caller that merely stopped waiting leaves a still-running start alone.
            if (ReferenceEquals(_ready, ready) && (ready.IsFaulted || ready.IsCanceled))
                _ready = null;
            throw;
        }
        ct.ThrowIfCancellationRequested();

        // Disposed between the start finishing and here (sign-out, shutdown): there is no session left.
        var host = _host ?? throw new OperationCanceledException("The hidden session was closed.");
        await ResumeAsync(host);

        // The wait inside ResumeAsync is where a disposal usually lands: the host it is about to run
        // on is gone by then, and running anything on it would fail in its own odd ways.
        if (_disposed || !ReferenceEquals(_host, host))
            throw new OperationCanceledException("The hidden session was closed.");

        var scriptTask = host.ExecuteScriptAsync(script, ct);
        if (!await WinsAgainstTimeoutAsync(scriptTask, _scriptTimeout, ct))
        {
            // The script call itself is abandoned here - the whole session is torn down so the
            // next attempt starts completely fresh instead of reusing a browser that may still be
            // busy with (or stuck on) this call, and the abandoned task's eventual fault is
            // observed so it never surfaces as an unobserved task exception.
            _ = scriptTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _host = null;
            _ready = null;
            await host.DisposeAsync();

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Script execution timed out.");
        }
        try
        {
            return await scriptTask;
        }
        finally
        {
            _lastActivityAt = _timeProvider.GetUtcNow();
            _suspendTask = SuspendQuietlyAsync(host);
        }
    }

    /// <summary>True when <paramref name="work"/> finished before <paramref name="timeout"/> ran out; false
    /// on the timeout or the caller's cancellation. The timer is cancelled as soon as the race is decided,
    /// so a finished call does not leave one running for the rest of the timeout.</summary>
    internal static async Task<bool> WinsAgainstTimeoutAsync(
        Task work, TimeSpan timeout, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = (delay ?? Task.Delay)(timeout, timerCts.Token);
        var winner = await Task.WhenAny(work, timer);
        timerCts.Cancel();
        return winner == work;
    }

    /// <summary>Lets the page rest between fetches: a suspended page keeps its session but gives its
    /// memory and CPU back. Never fails a fetch - a browser that cannot suspend just stays awake.</summary>
    private async Task SuspendQuietlyAsync(IHiddenBrowserHost host)
    {
        try
        {
            await host.SuspendAsync();
        }
        catch (Exception ex)
        {
            LogSuspendFailureOnce(ex);
        }
    }

    private async Task ResumeAsync(IHiddenBrowserHost host)
    {
        var pending = _suspendTask;
        _suspendTask = null;
        if (pending is null)
            return; // a fresh page was never suspended
        try
        {
            // Ends early when the runner is disposed meanwhile: nothing is left to resume.
            await pending.WaitAsync(_suspendWait, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            LogSuspendFailureOnce(ex);
        }
        finally
        {
            // Even when the suspend never finished: the script must not run against a page left hidden.
            try
            {
                host.Resume();
            }
            catch (Exception ex)
            {
                LogSuspendFailureOnce(ex);
            }
        }
    }

    private void LogSuspendFailureOnce(Exception ex)
    {
        if (_suspendFailureLogged)
            return;
        _suspendFailureLogged = true;
        LogService.Shared.LogInfo($"Hidden session: suspend or resume failed ({ex.GetType().Name}).");
    }

    private void ArmIdleTimer(TimeSpan? due = null)
    {
        if (_disposed || _hostContext is null)
            return;
        var timer = _timeProvider.CreateTimer(OnIdleTimer, null, due ?? IdleDisposalSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _idleTimer, timer)?.Dispose();
    }

    private void DisarmIdleTimer() => Interlocked.Exchange(ref _idleTimer, null)?.Dispose();

    private void OnIdleTimer(object? state) =>
        _hostContext?.Post(_ => _ = DisposeIfIdleAsync(), null);

    /// <summary>Runs on the thread that created the host. Re-checks what the timer could not know: a
    /// call may have started, or finished and restarted the clock, since it fired.</summary>
    private async Task DisposeIfIdleAsync()
    {
        if (_disposed || Volatile.Read(ref _inFlight) > 0 || _host is null)
            return;
        if (_lastActivityAt is not { } last)
            return;

        // A timer can fire slightly before the span is up by the clock this compares against: wait out
        // the rest instead of leaving the host to the next call.
        var remaining = IdleDisposalSpan - (_timeProvider.GetUtcNow() - last);
        if (remaining > TimeSpan.Zero)
        {
            ArmIdleTimer(remaining);
            return;
        }

        var host = _host;
        _host = null;
        _ready = null;
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Disposing the idle hidden browser failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>On any failure the half-built host is torn down and <c>_ready</c> is cleared, so the
    /// next call starts completely from scratch instead of replaying the same stale failure.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        var host = _hostFactory();
        var context = SynchronizationContext.Current;
        try
        {
            var navigateTask = host.NavigateAsync(ct);
            if (!await WinsAgainstTimeoutAsync(navigateTask, _navigationTimeout, ct))
            {
                _ = navigateTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Navigation to the provider's site timed out.");
            }
            if (!await navigateTask)
                throw new InvalidOperationException("Navigation to the provider's site failed.");

            // A disposal that ran while the start was waiting saw no host to close; keeping this one
            // would leave its browser open on the profile folder.
            if (_disposed || ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            _host = host;
            _hostContext = context;
            _lastActivityAt = _timeProvider.GetUtcNow();
        }
        catch
        {
            await host.DisposeAsync();
            _host = null;
            _ready = null;
            throw;
        }
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(waitForPendingStart: true);

    /// <summary>App shutdown: the caller blocks the UI thread, and a start still in flight can only
    /// finish on that thread, so waiting for it would hang the exit. Nothing deletes the profile
    /// folder afterwards, so there is nothing to wait for.</summary>
    public ValueTask DisposeForShutdownAsync() => DisposeCoreAsync(waitForPendingStart: false);

    private async ValueTask DisposeCoreAsync(bool waitForPendingStart)
    {
        _disposed = true;
        DisarmIdleTimer();
        var pendingStart = _ready;
        _lifetime.Cancel();
        _ready = null;

        // A start still in flight tears its own half-built host down once it sees the cancellation;
        // the owner is about to delete the profile folder, so wait for that to be finished. The start
        // is bounded by the navigation timeout and ends at the cancellation above.
        if (waitForPendingStart && pendingStart is not null)
        {
            try
            {
                await pendingStart;
            }
            catch
            {
                // The start's own failure reaches its callers; here only its end matters.
            }
        }

        if (_host is not null)
        {
            var host = _host;
            _host = null;
            await host.DisposeAsync();
        }
    }
}

/// <summary>
/// Seam between <see cref="WebSessionScriptRunner"/>'s own timeout and retry logic and the real
/// browser session, so that logic is unit-testable without a live WebView2. The one production
/// implementation is <see cref="WebView2HiddenBrowserHost"/>.
/// </summary>
internal interface IHiddenBrowserHost : IAsyncDisposable
{
    /// <summary>Navigates to the base page and waits for it to finish. Returns whether it
    /// succeeded; a normal navigation failure is a false result, not an exception.</summary>
    Task<bool> NavigateAsync(CancellationToken ct);

    Task<string> ExecuteScriptAsync(string script, CancellationToken ct);

    /// <summary>Puts the page to rest between fetches to free its memory and CPU. Throws on failure;
    /// the caller treats that as "stay awake".</summary>
    Task SuspendAsync();

    /// <summary>Wakes the page before the next script. Safe when it was never suspended.</summary>
    void Resume();
}

/// <summary>
/// The real hidden WebView2 session: off-screen, never shown, never in the taskbar or Alt-Tab list,
/// and never activated on show so a background refresh cannot steal keyboard focus from whatever the
/// user is typing in. CoreWebView2 needs some real window to attach to, so this owns one.
/// </summary>
internal sealed class WebView2HiddenBrowserHost : IHiddenBrowserHost
{
    private static readonly TimeSpan ReturnNavigationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Marks the answer of the origin guard in front of every script: the page the script was
    /// about to run on is not the provider's own site.</summary>
    internal const string OffOriginStatus = "__off_origin";

    /// <summary>Runs in every document of the hidden session before the page's own scripts. A page
    /// calling <c>print()</c> would open the browser's print dialog and hold the page's script thread until
    /// somebody closed it, and nobody ever sees this window. The function is replaced on the window itself,
    /// where it lives, and cannot be put back by the page. A frame the page adds itself starts with a
    /// blank document this script never reaches, so its <c>print()</c> is not covered; the timeout of the
    /// script run is the backstop there and ends the stall.</summary>
    internal const string DocumentCreatedScript =
        "Object.defineProperty(window,'print',{value:function(){},writable:false,configurable:false});";

    private readonly string _baseUrl;
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly string _usageOriginHost;
    private readonly WebViewHost _host;
    private readonly FirstPerHostGate _popupLogGate = new();
    private Window? _hiddenWindow;
    private WebView2? _webView;
    // Set by DisposeAsync, which may run while NavigateAsync is still awaiting the environment or the
    // browser: whatever it would build afterwards has no owner left to dispose it.
    private volatile bool _disposed;
    private TaskCompletionSource<bool>? _navigated;

    public WebView2HiddenBrowserHost(WebViewHost host, WebSessionDescriptor descriptor)
    {
        _host = host;
        _baseUrl = descriptor.BaseUrl;
        _allowedHosts = descriptor.AllowedHosts;
        _usageOriginHost = new Uri(descriptor.BaseUrl).Host;
    }

    public async Task<bool> NavigateAsync(CancellationToken ct)
    {
        // Defence in depth: this host is only ever navigated once in production (WebSessionScriptRunner
        // builds a fresh host per attempt), but a second call must never silently leak the previous
        // window and WebView2 instead of closing them.
        if (_hiddenWindow is not null || _webView is not null)
            DisposeBrowser();

        if (_disposed)
            return false;

        var environment = await _host.EnsureEnvironmentAsync().WaitAsync(ct);
        if (_disposed)
            return false;

        var webView = new WebView2();
        var hiddenWindow = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Content = webView,
        };
        _webView = webView;
        _hiddenWindow = hiddenWindow;
        hiddenWindow.Show();

        await webView.EnsureCoreWebView2Async(environment);
        if (_disposed || ct.IsCancellationRequested)
        {
            DisposeBrowser();
            ct.ThrowIfCancellationRequested();
            return false;
        }

        // Never shown, never interacted with by a person - so nothing here needs F12, a right-click
        // menu, or the browser's own accelerator keys, and every one of those is a way this hidden
        // session could otherwise be driven or inspected from outside this app's own code.
        var settings = webView.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;

        // No dialog of the page ever shows: an alert, confirm, prompt or beforeunload prompt nobody can
        // see or answer would hold every later fetch of this session.
        settings.AreDefaultScriptDialogsEnabled = false;
        webView.CoreWebView2.ScriptDialogOpening += (_, e) =>
        {
            if (ShouldAcceptScriptDialog(e.Kind))
                e.Accept();
        };

        // The same goes for a sign-in prompt of the site (HTTP authentication, a client certificate):
        // this session never answers one.
        webView.CoreWebView2.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        webView.CoreWebView2.ClientCertificateRequested += (_, e) => e.Cancel = true;
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DocumentCreatedScript);

        // A redirect chain out of a candidate/discovery response, or anything else this session did
        // not start out to reach, must never be followed with the usage script's own cookies along
        // for the ride - same allow-list the sign-in window uses, so the two never drift apart.
        webView.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (!SignInNavigationPolicy.IsAllowedUri(e.Uri, _allowedHosts))
                e.Cancel = true;
        };

        // This session reads numbers and nothing else: a page that tries to open a window, ask for a
        // permission (camera, location, notifications) or start a download gets a quiet no. Only
        // this hidden session; the visible sign-in window keeps its own behavior.
        webView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) ? target.Host : "unknown";
            // One line per host: a page opening windows in a loop must not push older diagnostics
            // out of the rotating log.
            if (_popupLogGate.IsFirst(host))
                LogService.Shared.LogInfo($"Hidden session: popup to {host} dropped.");
        };
        webView.CoreWebView2.PermissionRequested += (_, e) => e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;
        webView.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;

        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigated = navigated;
        webView.CoreWebView2.NavigationCompleted += (_, e) => navigated.TrySetResult(e.IsSuccess);
        webView.CoreWebView2.Navigate(_baseUrl);
        return await navigated.Task.WaitAsync(ct);
    }

    /// <summary>Which page dialogs the hidden session answers with "yes": only the leave-page prompt, so
    /// the app's own navigations (back to the base page) are never held by the page. Every other dialog
    /// is dismissed, which reads as "no" or as a cancelled prompt.</summary>
    internal static bool ShouldAcceptScriptDialog(Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind kind) =>
        kind == Microsoft.Web.WebView2.Core.CoreWebView2ScriptDialogKind.Beforeunload;

    public async Task SuspendAsync()
    {
        var webView = _webView;
        var core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        // A page can only be suspended while its controller counts as hidden; the control passes its
        // own visibility on to the controller.
        webView.Visibility = Visibility.Hidden;
        core.MemoryUsageTargetLevel = Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;
        await core.TrySuspendAsync();
    }

    public void Resume()
    {
        var webView = _webView;
        var core = webView?.CoreWebView2;
        if (webView is null || core is null)
            return;

        core.Resume();
        core.MemoryUsageTargetLevel = Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;
        webView.Visibility = Visibility.Visible;
    }

    public async Task<string> ExecuteScriptAsync(string script, CancellationToken ct)
    {
        // Belt and suspenders on top of the navigation gate above: the usage script itself never
        // runs against anything but this provider's own base host, so a session that ended up
        // somewhere else answers honestly instead of handing back whatever that other origin's
        // script produced. It is sent back to the base page, so the next fetch can succeed again
        // instead of every later call answering "blocked" for the rest of the process.
        //
        // Which of the two honest answers it is depends on where the session actually ended up: a
        // host on this provider's own sign-in list is the provider itself sending a session it does
        // not recognise to its login page, which is "not signed in" and offers the user a sign-in
        // button. Anything else really is a session steered somewhere it was never meant to go.
        //
        // The same check runs once more inside the page, in front of the script itself (see
        // WrapWithOriginGuard): this one only saves the round trip, that one cannot be overtaken by a
        // navigation that lands between the check and the script.
        var core = _webView?.CoreWebView2;
        if (core is null || _disposed)
            throw new OperationCanceledException("The hidden browser was closed.");

        if (!SignInNavigationPolicy.IsUsageOrigin(core.Source, _usageOriginHost))
        {
            var envelope = OffOriginEnvelope(core.Source, _allowedHosts);
            await NavigateAndWaitAsync(core, _baseUrl, ct);
            return envelope;
        }

        // Runtime.evaluate rather than ExecuteScriptAsync: both scripts are async functions, and
        // ExecuteScriptAsync hands back a pending Promise as "{}" instead of waiting for its value.
        // Neither call has a CancellationToken overload - racing it against ct means a cancelled fetch
        // (sign-out, shutdown) returns promptly instead of waiting out the caller's own timeout.
        var parameters = JsonSerializer.Serialize(new
        {
            expression = WrapWithOriginGuard(script, _usageOriginHost),
            awaitPromise = true,
            returnByValue = true,
        });
        var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters).WaitAsync(ct);
        var answer = UnwrapEvaluateResult(raw);

        if (ReadOffOriginAddress(answer) is { } address)
        {
            var envelope = OffOriginEnvelope(address, _allowedHosts);
            await NavigateAndWaitAsync(core, _baseUrl, ct);
            return envelope;
        }

        return answer;
    }

    /// <summary>The script with a host check in front, evaluated in the same step as the script itself:
    /// on any page but the provider's own site (https, the host or one of its subdomains) the script
    /// does not run and the answer names the address instead, for <see cref="OffOriginEnvelope"/> to
    /// classify. Without it, a navigation landing between a check made from outside and the evaluation
    /// would run the script - which fetches with the session's cookies - against a foreign page. The
    /// guard runs in the page's own world, so it uses no method of a built-in prototype (a page can
    /// replace those): only the length of the host name, single characters and <c>===</c>.</summary>
    internal static string WrapWithOriginGuard(string script, string usageHost)
    {
        var host = JsonSerializer.Serialize(usageHost);
        // A block rather than a wrapping function: a script may open with function declarations before its
        // own async call, and the value of the last statement is what the evaluation hands back. The
        // comparison itself sits in an arrow function so it declares nothing in the page's global scope.
        return $$"""
            if (!((h, u) => {
                if (location.protocol !== 'https:') return false;
                if (h === u) return true;
                const d = h.length - u.length - 1;
                if (d < 1 || h[d] !== '.') return false;
                for (let i = 0; i < u.length; i++) if (h[d + 1 + i] !== u[i]) return false;
                return true;
            })(location.hostname, {{host}}))
                ({status: '{{OffOriginStatus}}', href: location.href});
            else {
            {{script}}
            }
            """;
    }

    /// <summary>The address the origin guard reported, or null when the answer is the script's own.</summary>
    internal static string? ReadOffOriginAddress(string answer)
    {
        if (answer.Length == 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                && status.GetString() == OffOriginStatus
                && root.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String
                    ? href.GetString() ?? ""
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends the session back to the base page and waits for that navigation to finish, so the
    /// next call never evaluates against the document being torn down. A page that does not finish
    /// within <see cref="ReturnNavigationTimeout"/> is left to load on its own.</summary>
    private static async Task NavigateAndWaitAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();

        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            await done.Task.WaitAsync(ReturnNavigationTimeout, ct);
        }
        catch (TimeoutException)
        {
            // Left to finish by itself.
        }
        finally
        {
            try
            {
                core.NavigationCompleted -= OnCompleted;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
            {
                // The session was disposed meanwhile; nothing left to unhook.
            }
        }
    }

    /// <summary>What a session that is not on its own usage page answers with. A host on this
    /// provider's own sign-in list is the provider itself sending a session it does not recognise to
    /// its login page: that is "not signed in", and the tile offers a sign-in button for it. Any
    /// other host is a session steered somewhere it was never meant to go, which stays "blocked".</summary>
    internal static string OffOriginEnvelope(string source, IReadOnlyList<string> allowedHosts) =>
        SignInNavigationPolicy.IsAllowedUri(source, allowedHosts)
            ? """{"status":"not_signed_in"}"""
            : """{"status":"blocked"}""";

    /// <summary>The script's own returned value as JSON text, out of a Runtime.evaluate response;
    /// empty (read as a failed fetch) for a thrown script or an unexpected shape.</summary>
    internal static string UnwrapEvaluateResult(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("exceptionDetails", out _))
                return "";
            return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("value", out var value)
                ? value.GetRawText()
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        DisposeBrowser();
        return _host.DisposeAsync();
    }

    /// <summary>The only place that clears <see cref="_hiddenWindow"/> and <see cref="_webView"/>.
    /// An undisposed WebView2 control collected by the GC has its HwndHost finalizer run
    /// WebView2Base.Uninitialize on the finalizer thread, where CoreWebView2Controller.get_CoreWebView2()
    /// marshals into the STA-bound COM object and throws - so this disposes the control on the window's
    /// own dispatcher, while its own UI thread is still the one running, before the window is closed.
    /// A dispatcher already shutting down (app exit, a window closed on its own) is an expected state
    /// here, not an error to throw on.</summary>
    private void DisposeBrowser()
    {
        // A navigation still waiting for its completion event never gets one once the page is gone.
        _navigated?.TrySetResult(false);
        _navigated = null;
        var webView = _webView;
        var hiddenWindow = _hiddenWindow;
        _webView = null;
        _hiddenWindow = null;
        if (webView is null && hiddenWindow is null)
            return;

        void DisposeNow()
        {
            if (hiddenWindow is not null)
                hiddenWindow.Content = null;
            webView?.Dispose();
            hiddenWindow?.Close();
        }

        try
        {
            var dispatcher = hiddenWindow?.Dispatcher ?? webView?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.Invoke(DisposeNow);
            else
                DisposeNow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            LogService.Shared.LogError("Disposing the hidden browser failed.");
        }
    }
}

/// <summary>Answers true once per host name, so a repeated event is logged a single time. Bounded:
/// past a fixed number of distinct hosts nothing more is reported.</summary>
internal sealed class FirstPerHostGate
{
    private const int MaxHosts = 20;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFirst(string host)
    {
        lock (_seen)
            return _seen.Count < MaxHosts && _seen.Add(host);
    }
}
