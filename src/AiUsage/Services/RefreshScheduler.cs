using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Ticks every registered provider on its own schedule and reports each result the moment it
/// arrives. Never awaits a provider before starting the next one, so one
/// slow or hanging provider never delays another provider's tile or the caller.
/// </summary>
public sealed class RefreshScheduler
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private const string TimeoutDetailKey = "State.Failed.Detail.Timeout";

    // A mutable list, not the IReadOnlyList the constructor still accepts: AddProvider/RemoveProvider
    // (the Settings.AddAccount and Settings.RemoveAccount actions) change it after
    // construction - every read and write of it happens under _gate, same as _state below.
    private readonly List<IUsageProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ProviderState> _state;
    private readonly TimeSpan _minFetchTimeout;
    private readonly TimeSpan _maxFetchTimeout;
    private readonly Action<string>? _log;

    // Every read and write of a ProviderState's fields (and of _baseInterval) takes this - Tick's
    // one-second timer reads them from the UI thread while RunOneAsync writes them from whichever
    // pool thread a provider's fetch happened to complete on, and TimeSpan/DateTimeOffset are
    // multi-field structs that can be seen half-written without a lock. FetchAsync itself is always
    // awaited, and SnapshotReady always raised, outside the lock - neither may ever hold it.
    private readonly object _gate = new();
    private TimeSpan _baseInterval;

    // Set by the most recent Tick/RefreshNow(all) call, read by RunOneAsync (directly, and via the
    // single-provider RefreshNow below, which has no fresher information of its own to pass in) to
    // resolve the interval a just-finished fetch schedules its next one at. The one-second timer
    // keeps these within a second of the real window/tile state, so a manual single-provider refresh
    // in between two ticks still resolves against an answer that is practically current.
    private Func<string, bool>? _isHidden;
    private bool _windowVisible = true;

    /// <summary>Same remembered-per-tick pattern as <see cref="_isHidden"/>: which accounts
    /// the Settings.SignOut action has disconnected, read by <see cref="RunOneCoreAsync"/> to skip a fetch entirely
    /// rather than only lengthen its interval.</summary>
    private Func<string, bool>? _isDisconnected;

    // Power and pause state, owned by the caller and read under _gate. The power state is only ever
    // pushed in (SetPower), never polled here, so a test drives it with a plain value.
    private PowerState _power;
    private bool _saveEnergyOnBattery;
    private DateTimeOffset? _pausedUntil;

    /// <summary>
    /// <paramref name="minFetchTimeout"/>/<paramref name="maxFetchTimeout"/> default to the real
    /// 60s/5min floor and cap; a test can inject a much shorter pair so a "provider never completes"
    /// scenario does not have to wait out a real 60 real seconds - the timeout itself always fires on
    /// the real clock (CancellationTokenSource has no fake-time hook), only the configured duration is
    /// test-controlled. <paramref name="log"/> receives one line for each subscriber that throws.
    /// </summary>
    public RefreshScheduler(IReadOnlyList<IUsageProvider> providers, TimeSpan baseInterval, TimeProvider? timeProvider = null,
        TimeSpan? minFetchTimeout = null, TimeSpan? maxFetchTimeout = null, Action<string>? log = null)
    {
        _log = log;
        _providers = providers.ToList();
        _baseInterval = baseInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minFetchTimeout = minFetchTimeout ?? TimeSpan.FromSeconds(60);
        _maxFetchTimeout = maxFetchTimeout ?? TimeSpan.FromMinutes(5);

        var now = _timeProvider.GetUtcNow();
        _state = _providers.ToDictionary(p => p.AccountKey,
            p => new ProviderState(ResolveInterval(baseInterval, p.MinRefreshInterval, isHidden: false, windowVisible: true), now));
    }

    /// <summary>Adds a provider that did not exist when this scheduler was built (the
    /// Settings.AddAccount action) - due for its first fetch immediately, same as any provider at
    /// construction.</summary>
    internal void AddProvider(IUsageProvider provider)
    {
        lock (_gate)
        {
            _providers.Add(provider);
            _state[provider.AccountKey] = new ProviderState(ResolveIntervalLocked(provider), _timeProvider.GetUtcNow());
        }
    }

    /// <summary>Removes a provider whose account was just removed (the Settings.RemoveAccount
    /// action) - it is simply never scheduled again afterward. Safe even while its fetch is still
    /// in flight: that fetch still runs to completion and still raises <see cref="FetchStarted"/>/
    /// <see cref="SnapshotReady"/> once more for it (every subscriber already tolerates an id it no
    /// longer knows, see <see cref="ViewModels.MainViewModel.IsTileHidden"/>).</summary>
    internal void RemoveProvider(string accountKey)
    {
        lock (_gate)
        {
            _providers.RemoveAll(p => p.AccountKey == accountKey);
            _state.Remove(accountKey);
        }
    }

    /// <summary>Tells one account's provider that a sign-in just finished (see <see
    /// cref="IUsageProvider.SignInCompleted"/>).</summary>
    internal void SignInCompleted(string accountKey)
    {
        IUsageProvider? provider;
        lock (_gate)
            provider = _providers.Find(p => p.AccountKey == accountKey);
        provider?.SignInCompleted();
    }

    /// <summary>Raised once per provider as soon as that provider's own fetch completes.</summary>
    public event Action<ProviderSnapshot>? SnapshotReady;

    /// <summary>Raised for a provider the instant its own fetch actually starts - before the await, so
    /// a caller can show "this one is working" immediately instead of waiting for the result. Not
    /// marshalled onto any particular thread here, same as <see cref="SnapshotReady"/> - a subscriber
    /// that needs the UI thread does its own dispatching.</summary>
    public event Action<string>? FetchStarted;

    /// <summary>Raised instead of <see cref="SnapshotReady"/> when the owner cancelled a fetch on
    /// purpose (<see cref="CancelProvider"/>): the attempt is over, but no reading comes out of it, so a
    /// caller that showed "working" at <see cref="FetchStarted"/> must end it here.</summary>
    public event Action<string>? FetchEnded;

    /// <summary>
    /// Changes the base interval the Settings window's refresh interval slider controls.
    /// Takes effect from each provider's next completed fetch onward (<see cref="RunOneAsync"/> reads
    /// <see cref="_baseInterval"/> fresh every time) - a provider already waiting keeps its current
    /// wait rather than being retroactively rescheduled.
    /// </summary>
    public void UpdateBaseInterval(TimeSpan interval)
    {
        lock (_gate)
            _baseInterval = interval;
    }

    /// <summary>Tells the scheduler what the machine's power looks like. The interval change shows from
    /// each provider's next completed fetch; the energy-saver skip applies at once.</summary>
    public void SetPower(PowerState power)
    {
        lock (_gate)
            _power = power;
    }

    /// <summary>The "save energy on battery" setting: on battery the intervals double, and while the
    /// energy saver is on only local sources are read.</summary>
    public void SetSaveEnergyOnBattery(bool enabled)
    {
        lock (_gate)
            _saveEnergyOnBattery = enabled;
    }

    /// <summary>Stops every fetch that uses the network for <paramref name="duration"/>, counted from
    /// now; local files keep updating. Returns the moment the pause ends. Not remembered across
    /// restarts.</summary>
    public DateTimeOffset PauseFor(TimeSpan duration)
    {
        lock (_gate)
        {
            var until = _timeProvider.GetUtcNow() + duration;
            _pausedUntil = until;
            return until;
        }
    }

    /// <summary>Ends a pause early.</summary>
    public void Resume()
    {
        lock (_gate)
            _pausedUntil = null;
    }

    /// <summary>When the running pause ends, or null when none runs (an expired one counts as over).</summary>
    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_gate)
                return PausedUntilLocked(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>Caller must already hold <see cref="_gate"/>.</summary>
    private DateTimeOffset? PausedUntilLocked(DateTimeOffset now) => _pausedUntil is { } until && now < until ? until : null;

    /// <summary>
    /// Starts every due, not-already-running provider in parallel - a hidden tile or a closed window
    /// never skips a provider outright, they only ever lengthen its interval (see <see
    /// cref="ResolveInterval"/>), so history stays continuous. Returns the tasks it just started so a
    /// caller (or a test) can await them explicitly; ignoring the return value is fine - each task
    /// reports itself via <see cref="SnapshotReady"/> regardless.
    /// </summary>
    public IReadOnlyList<Task> Tick(Func<string, bool>? isHidden = null, bool windowVisible = true,
        Func<string, bool>? isDisconnected = null, CancellationToken ct = default) =>
        TickCore(isHidden, windowVisible, isDisconnected, ignoreNetworkSkip: false, ct);

    private List<Task> TickCore(Func<string, bool>? isHidden, bool windowVisible,
        Func<string, bool>? isDisconnected, bool ignoreNetworkSkip, CancellationToken ct)
    {
        List<IUsageProvider> due;
        lock (_gate)
        {
            // Remembered for RunOneAsync (including a later single-provider RefreshNow, which has no
            // fresher answer of its own) - see the fields' own doc comment.
            _isHidden = isHidden;
            _windowVisible = windowVisible;
            _isDisconnected = isDisconnected;

            // The due check and the InFlight claim happen inside the same lock acquisition - two
            // callers arriving together (the one-second timer and a manual refresh, say) must never
            // both see a provider as due and both start it.
            var now = _timeProvider.GetUtcNow();
            due = _providers.Where(p => IsDueLocked(p, now, ignoreNetworkSkip)).ToList();
            foreach (var provider in due)
                _state[provider.AccountKey].InFlight = true;
        }

        return due.Select(provider => RunOneAsync(provider, ct)).ToList();
    }

    /// <summary>
    /// Forces every not-already-running provider to fetch now, bypassing its remaining wait once
    /// (F5 / tray "refresh now"). Normal per-provider scheduling resumes
    /// right after this fetch completes.
    /// </summary>
    public IReadOnlyList<Task> RefreshNow(Func<string, bool>? isHidden = null, bool windowVisible = true,
        Func<string, bool>? isDisconnected = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var state in _state.Values.Where(s => !s.InFlight))
                state.NextDueAt = now;
        }
        // A manual refresh is a fetch the person asked for: it reads every source once, even
        // while paused or on the energy saver, and leaves both in place.
        return TickCore(isHidden, windowVisible, isDisconnected, ignoreNetworkSkip: true, ct);
    }

    /// <summary>
    /// Forces exactly one provider to fetch now, bypassing its remaining wait once - including its own
    /// web floor - without touching any other provider's schedule. An unknown or already-in-flight id
    /// is a no-op, never an exception.
    /// </summary>
    public IReadOnlyList<Task> RefreshNow(string providerId, CancellationToken ct = default)
    {
        IUsageProvider? provider;
        lock (_gate)
        {
            if (!_state.TryGetValue(providerId, out var state) || state.InFlight)
                return [];
            provider = _providers.FirstOrDefault(p => p.AccountKey == providerId);
            if (provider is null)
                return [];
            state.NextDueAt = _timeProvider.GetUtcNow();
            state.InFlight = true;
        }
        return [RunOneAsync(provider, ct)];
    }

    /// <summary>
    /// Cancels whichever fetch is currently in flight for one provider (sign-out: the browser
    /// profile folder it reads from must not be deleted out from under a still-running fetch) and
    /// waits for that fetch's own RunOneAsync to actually finish - not merely for the cancellation
    /// to be requested - before returning. A provider with nothing in flight returns immediately.
    /// </summary>
    internal async Task CancelProvider(string providerId)
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (_gate)
        {
            if (!_state.TryGetValue(providerId, out var state))
                return;
            cancellation = state.RunCancellation;
            running = state.RunningTask;
            // Tells the running attempt this cancellation is the owner's, not a timeout.
            if (cancellation is not null)
                state.CancelledByOwner = true;
        }
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The fetch finished and disposed its token source between the lookup above and here -
            // nothing left to cancel.
        }
        if (running is not null)
        {
            try
            {
                await running;
            }
            catch
            {
                // RunOneAsync already turns every failure (including this cancellation) into a
                // reported Failed/NotSignedIn snapshot and never actually lets an exception escape
                // its own Task - this catch is defence in depth only.
            }
        }
    }

    /// <summary>Caller must already hold <see cref="_gate"/>.</summary>
    private bool IsDueLocked(IUsageProvider provider, DateTimeOffset now, bool ignoreNetworkSkip = false)
    {
        var state = _state[provider.AccountKey];
        if (state.InFlight || now < state.NextDueAt)
            return false;
        // A skipped provider keeps its past due time, so it is fetched on the first tick after the
        // pause or the energy saver ends.
        return ignoreNetworkSkip || !NetworkIsOffLocked(now) || IsLocalSource(state);
    }

    /// <summary>Whether fetches that use the network are held back right now: a pause is running, or
    /// the energy saver is on and the setting asks to honour it. Caller must already hold <see
    /// cref="_gate"/>.</summary>
    private bool NetworkIsOffLocked(DateTimeOffset now) =>
        PausedUntilLocked(now) is not null || (_saveEnergyOnBattery && _power.EnergySaver);

    /// <summary>Local means the provider's last real reading came from a file or a database. A provider
    /// that has not delivered one yet counts as local, so it is fetched once to find out.</summary>
    private static bool IsLocalSource(ProviderState state) =>
        state.LastSource is null or SourceKind.LocalFile or SourceKind.LocalDatabase;

    /// <summary>CancelProvider stopped this attempt on purpose: report no reading and keep the
    /// interval (the owner is about to change or remove the account), but tell the subscribers the
    /// attempt is over.</summary>
    private void EndOwnerCancelled(IUsageProvider provider, ProviderState state)
    {
        lock (_gate)
            state.NextDueAt = _timeProvider.GetUtcNow() + state.CurrentInterval;
        FetchEnded?.Invoke(provider.AccountKey);
    }

    private bool OwnerCancelled(ProviderState state)
    {
        lock (_gate)
            return state.CancelledByOwner;
    }

    private async Task RunOneAsync(IUsageProvider provider, CancellationToken ct)
    {
        ProviderState? state;
        lock (_gate)
        {
            // RemoveProvider may have dropped this provider between the claim and this point.
            if (!_state.TryGetValue(provider.AccountKey, out state))
                return;
        }
        var attemptStartedAt = _timeProvider.GetUtcNow();
        var runTask = RunOneCoreAsync(provider, state, attemptStartedAt, ct);
        lock (_gate)
            state.RunningTask = runTask;
        try
        {
            await runTask;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.RunningTask, runTask))
                    state.RunningTask = null;
            }
        }
    }

    private async Task RunOneCoreAsync(IUsageProvider provider, ProviderState state, DateTimeOffset attemptStartedAt, CancellationToken ct)
    {
        // Disposed by hand in the finally below rather than with "using": when the caller's token is
        // cancelled synchronously, the WaitAsync continuation can run inline and reach this method's
        // end before the linked cancellation has been passed on - a "using" disposal at that point
        // unregistered the link, and the provider's own token was never cancelled at all.
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // A disconnected account (the Settings.SignOut action) is read through no source at all - not
            // the web session, not a local file, not a CLI call - so this returns before even raising
            // FetchStarted, which would otherwise show the tile's spinner for a fetch that never runs.
            if (_isDisconnected?.Invoke(provider.AccountKey) == true)
            {
                lock (_gate)
                {
                    state.CurrentInterval = ResolveIntervalLocked(provider);
                    state.NextDueAt = _timeProvider.GetUtcNow() + state.CurrentInterval;
                }
                RaiseSnapshotReady(new ProviderSnapshot(provider.AccountKey, [], null, SourceKind.None,
                    attemptStartedAt, null, ProviderStatus.NotSignedIn, null));
                return;
            }

            RaiseFetchStarted(provider.AccountKey);

            TimeSpan baseForTimeout;
            lock (_gate)
                baseForTimeout = ResolveIntervalLocked(provider);
            var timeout = Clamp(baseForTimeout * 2, _minFetchTimeout, _maxFetchTimeout);

            ProviderSnapshot snapshot;
            lock (_gate)
            {
                state.RunCancellation = timeoutCts;
                state.CancelledByOwner = false;
            }
            timeoutCts.CancelAfter(timeout);
            Task<ProviderSnapshot>? fetchTask = null;
            try
            {
                // The timeout is enforced here, by the caller, rather than relying solely on the
                // provider observing timeoutCts.Token - a provider whose returned task never
                // completes (and never checks its token) would otherwise hang this await forever,
                // leaving InFlight stuck and the provider never due again.
                //
                // Most providers do real synchronous work (a directory walk, reading session files)
                // before their first await, which would otherwise run on this method's own caller -
                // the UI thread's dispatcher timer. Starting the fetch on the thread pool keeps that
                // off the UI thread. The one provider that must not move is the web-session-backed
                // one: it already marshals its own work and expects to start on the calling thread.
                fetchTask = provider.RunsOnUiThread
                    ? provider.FetchAsync(timeoutCts.Token)
                    : Task.Run(() => provider.FetchAsync(timeoutCts.Token), timeoutCts.Token);
                snapshot = await fetchTask.WaitAsync(timeout, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && OwnerCancelled(state))
            {
                EndOwnerCancelled(provider, state);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own timeout fired (the caller's own token was not cancelled) - a provider
                // whose fetch never completes must still free itself up for the next attempt instead
                // of staying InFlight forever.
                snapshot = new ProviderSnapshot(provider.AccountKey, [], null, SourceKind.None, attemptStartedAt, null,
                    ProviderStatus.Failed, FailedError(TimeoutDetailKey, kind: FailureKind.Timeout));
            }
            catch (TimeoutException)
            {
                // WaitAsync's own timeout fired because the provider's task is still running and
                // ignored timeoutCts.Token entirely. The task is abandoned here - observe its
                // eventual fault (if any) so it never surfaces as an unobserved task exception.
                _ = fetchTask?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                snapshot = new ProviderSnapshot(provider.AccountKey, [], null, SourceKind.None, attemptStartedAt, null,
                    ProviderStatus.Failed, FailedError(TimeoutDetailKey, kind: FailureKind.Timeout));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FetchAsync's contract is "never throws" - this is defence in depth against a violation.
                snapshot = new ProviderSnapshot(provider.AccountKey, [], null, SourceKind.None, attemptStartedAt, null,
                    ProviderStatus.Failed, ex switch
                    {
                        System.Net.Http.HttpRequestException { StatusCode: { } status } =>
                            FailedError(
                                "State.Failed.Detail.Http", ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ProviderError.KindForStatus((int)status), (int)status),
                        System.Net.Http.HttpRequestException => FailedError("State.Failed.Detail.Network", kind: FailureKind.Network),
                        _ => FailedError(),
                    });
            }

            // A provider that swallowed the owner's cancellation and answered with a failure must not
            // be reported or backed off either: the answer belongs to an attempt that was abandoned.
            if (!ct.IsCancellationRequested && OwnerCancelled(state))
            {
                EndOwnerCancelled(provider, state);
                return;
            }

            lock (_gate)
            {
                var baseForProvider = ResolveIntervalLocked(provider);
                state.CurrentInterval = snapshot.Status == ProviderStatus.Failed
                    ? Max(baseForProvider, Min(state.CurrentInterval * 2, MaxBackoff))
                    : baseForProvider;
                // From completion, not attemptStartedAt: a fetch that itself took longer than its
                // own interval must not be immediately due again the moment it finishes.
                state.NextDueAt = _timeProvider.GetUtcNow() + state.CurrentInterval;
                // A failure carries no source; the last real one stays the answer.
                if (snapshot.SourceKind != SourceKind.None)
                    state.LastSource = snapshot.SourceKind;
            }

            RaiseSnapshotReady(snapshot);
        }
        finally
        {
            // Always clears, even if FetchAsync's own cancellation propagates past the catch above -
            // otherwise this provider would never be considered due again for the rest of the run.
            lock (_gate)
            {
                state.InFlight = false;
                state.RunCancellation = null;
            }

            // Whatever is still running under this token (an abandoned, timed-out fetch, or one the
            // caller cancelled) is told to stop before the source goes away.
            timeoutCts.Cancel();
            timeoutCts.Dispose();
        }
    }

    private void RaiseSnapshotReady(ProviderSnapshot snapshot) => RaiseEach(SnapshotReady, handler => handler(snapshot));

    private void RaiseFetchStarted(string accountKey) => RaiseEach(FetchStarted, handler => handler(accountKey));

    /// <summary>Calls each subscriber on its own: one that throws is logged and neither skips the
    /// others nor loses the snapshot or the fetch it was told about.</summary>
    private void RaiseEach<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((T)(object)handler);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Invoke($"A refresh subscriber failed ({ex.GetType().Name}): {PathSanitizer.Sanitize(ex.Message)}");
            }
        }
    }

    /// <summary>Caller must already hold <see cref="_gate"/>.</summary>
    private TimeSpan ResolveIntervalLocked(IUsageProvider provider) =>
        ResolveInterval(_baseInterval, provider.MinRefreshInterval, _isHidden?.Invoke(provider.AccountKey) ?? false, _windowVisible,
            onBattery: _saveEnergyOnBattery && _power.OnBattery);

    /// <summary>
    /// One shared rule for how long to wait before the next fetch, applied in order, each step only
    /// ever lengthening the wait: the base interval (Settings' slider) - a provider's own floor, e.g.
    /// the Claude web session's 5 min minimum - at least 5 min once the window itself is not visible -
    /// at least 15 min once the provider's own tile is hidden. A hidden, closed-window provider with a
    /// 5-minute floor lands on 15 min, not 5+5+15 - the rules raise a shared floor, they never add up.
    /// On battery (with the saving enabled) the result is then doubled.
    /// </summary>
    internal static TimeSpan ResolveInterval(TimeSpan baseInterval, TimeSpan? providerFloor, bool isHidden, bool windowVisible,
        bool onBattery = false)
    {
        var interval = baseInterval;
        if (providerFloor is { } floor && floor > interval)
            interval = floor;
        if (!windowVisible && interval < TimeSpan.FromMinutes(5))
            interval = TimeSpan.FromMinutes(5);
        if (isHidden && interval < TimeSpan.FromMinutes(15))
            interval = TimeSpan.FromMinutes(15);
        return onBattery ? interval * 2 : interval;
    }

    /// <summary>The error of a read the scheduler itself gave up on: the generic failed wording, the
    /// retry action, and the cause and its kind when known.</summary>
    private static ProviderError FailedError(
        string? detailKey = null, string? detailArg = null, FailureKind kind = FailureKind.Other, int? httpStatus = null) =>
        new("Status_Failed_Reason", "Action_Retry", detailKey, detailArg, kind, httpStatus);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private sealed class ProviderState(TimeSpan initialInterval, DateTimeOffset dueAt)
    {
        public TimeSpan CurrentInterval = initialInterval;
        public DateTimeOffset NextDueAt = dueAt;
        public bool InFlight;

        // The source of the last reading that had one; null until the first.
        public SourceKind? LastSource;

        // Both set at the start of RunOneAsync/RunOneCoreAsync and cleared once that same attempt
        // finishes - CancelProvider reads them to cancel and wait for whichever attempt is currently
        // running, without either RunOneAsync or CancelProvider ever holding _gate across an await.
        public CancellationTokenSource? RunCancellation;
        public Task? RunningTask;

        // Set by CancelProvider, reset when the next attempt starts.
        public bool CancelledByOwner;
    }
}
