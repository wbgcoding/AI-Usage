using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>
/// Runs <see cref="StatsIndexer.IndexOnce"/> on its own low priority background thread, so a first
/// walk over years of session logs never competes with the UI thread for CPU time. One instance per
/// running app: a <see cref="StartInBackground"/> call while a walk is already running is a no op,
/// but a walk that already finished can be started again, so usage made while the app keeps running
/// still reaches the statistics without waiting for a restart. <see cref="IndexCompleted"/> fires
/// after a walk that changed something (or the first one) so an open statistics window can reload
/// right away.
/// </summary>
public sealed class StatsIndexerService : IDisposable
{
    // Null result: the walk's outcome is unknown (the test seam, or a cancelled/failed walk).
    private readonly Func<CancellationToken, StatsIndexResult?> _runIndexOnce;
    private readonly Func<StatsDataSnapshot>? _loadSnapshot;
    private readonly LogService? _logService;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _thread;
    // Cleared under _gate before IndexCompleted fires, in RunSafely's own finally block - checking
    // this instead of _thread.IsAlive closes a race a restart right after the walk's own delegate
    // returns could otherwise hit: IsAlive only flips to false once the whole thread body has
    // unwound, a moment after the delegate itself already returned.
    private bool _isRunning;

    // Set once a walk has finished normally: that first one always signals, since the day grid and a
    // statistics window have nothing loaded before it.
    private bool _firstWalkDone;

    // The local date of the last announced walk: a new day is announced even when nothing was read,
    // since the day grid and the statistics window anchor "today" on the load they get.
    private DateOnly _lastRaisedDate;
    private Func<DateOnly> _today = () => DateOnly.FromDateTime(DateTime.Now);

    // The prepared data of the last raising walk. Every change bumps the generation, so a slow load
    // that was overtaken by a newer one never replaces it.
    private StatsDataSnapshot? _latest;
    private int _snapshotGeneration;

    /// <summary>Raised on a background thread after a walk finishes (cancelled or not) that read at
    /// least one file, after the first walk of the process whatever it read, after the first walk on a
    /// new local day, and after <see cref="RefreshSnapshotInBackground"/> has reloaded the data. A walk
    /// that found everything unchanged raises nothing otherwise: there is nothing new to load. A
    /// subscriber that touches UI state must marshal back to its own dispatcher itself.</summary>
    public event EventHandler? IndexCompleted;

    /// <summary>The instance <see cref="App"/> starts at launch and keeps for the app's whole
    /// lifetime. <see cref="Views.StatsWindow"/> has no constructor path back to <c>App.xaml.cs</c> -
    /// the same reason <see cref="Services.WindowPlacementService.Shared"/> exists - so it reads this
    /// static instead, to start a fresh walk when it opens and reload once one completes. Null in
    /// every test that never sets it, which then simply skips that wiring.</summary>
    public static StatsIndexerService? Shared { get; set; }

    /// <summary>The data as of the last walk that announced itself, or null before the first one and
    /// while a refresh is under way. A window that opens shows it at once instead of reading the
    /// store.</summary>
    public StatsDataSnapshot? Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    public StatsIndexerService(StatsStore store, LogService? logService = null)
        : this(token => new StatsIndexer(store, logService).IndexOnce(token), () => StatsDataSnapshot.Load(store), logService)
    {
    }

    /// <summary>Test seam: a controllable stand-in for the real walk, so the one-instance and
    /// cancel-aborts-it contracts can be driven and observed without touching this machine's own
    /// session history.</summary>
    internal StatsIndexerService(Action<CancellationToken> runIndexOnce, LogService? logService = null)
        : this(token =>
        {
            runIndexOnce(token);
            return (StatsIndexResult?)null;
        }, null, logService)
    {
    }

    /// <summary>Test seam like the one above, but the stand-in reports what the walk did, so the
    /// "only announce a walk that read something" rule can be driven.</summary>
    internal static StatsIndexerService ForTest(
        Func<CancellationToken, StatsIndexResult> runIndexOnce, Func<DateOnly>? today = null,
        Func<StatsDataSnapshot>? loadSnapshot = null, LogService? logService = null) =>
        new(token => runIndexOnce(token), loadSnapshot, logService) { _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now)) };

    private StatsIndexerService(Func<CancellationToken, StatsIndexResult?> runIndexOnce, Func<StatsDataSnapshot>? loadSnapshot, LogService? logService)
    {
        _runIndexOnce = runIndexOnce;
        _loadSnapshot = loadSnapshot;
        _logService = logService;
    }

    internal bool IsRunning
    {
        get
        {
            lock (_gate)
                return _isRunning;
        }
    }

    /// <summary>A walk is announced when it read at least one file - nothing in the index changed
    /// otherwise - when it is the first one to finish, or when the local date moved on since the last
    /// announcement. A walk without a result (cancelled or failed) may have read files, so it is
    /// announced too.</summary>
    internal static bool ShouldRaise(StatsIndexResult? result, bool firstWalkDone, bool dateChanged) =>
        result is not { } walk || !firstWalkDone || dateChanged || walk.Claude.FilesRead > 0 || walk.Codex.FilesRead > 0 || walk.Gemini.FilesRead > 0;

    public void StartInBackground()
    {
        lock (_gate)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _thread = new Thread(() => RunSafely(token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "StatsIndexer",
            };
            _thread.Start();
        }
    }

    private void RunSafely(CancellationToken token)
    {
        var raise = true;
        try
        {
            var result = _runIndexOnce(token);
            lock (_gate)
            {
                raise = ShouldRaise(result, _firstWalkDone, _today() != _lastRaisedDate);
                _firstWalkDone = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // This is a raw thread: anything escaping here would end the whole app.
            _logService?.LogError($"Statistics indexing failed ({ex.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (raise)
                {
                    lock (_gate)
                        _lastRaisedDate = _today();
                    // Before the event, so a window that reacts to it finds the new data waiting. A walk
                    // cancelled at shutdown skips it: nobody reads it, and it would hold up the exit.
                    if (!token.IsCancellationRequested)
                        RefreshSnapshot();
                    RaiseIndexCompleted();
                }
            }
            finally
            {
                // Last, so no second walk starts while this thread still loads the snapshot or
                // raises the event: Dispose then waits for the one thread that is left.
                lock (_gate)
                    _isRunning = false;
            }
        }
    }

    /// <summary>Drops the prepared data at once and loads it again on a pool thread, for a change to
    /// the store that no walk announces (an imported or removed PC). The window reads the store itself
    /// while <see cref="Latest"/> is null. Raises <see cref="IndexCompleted"/> once the load is over,
    /// also when it failed, so an open window reloads.</summary>
    public void RefreshSnapshotInBackground()
    {
        if (_loadSnapshot is null)
            return;

        var generation = ClearSnapshot();
        Task.Run(() =>
        {
            LoadSnapshot(generation);
            RaiseIndexCompleted();
        });
    }

    private void RefreshSnapshot()
    {
        if (_loadSnapshot is null)
            return;

        int generation;
        lock (_gate)
            generation = ++_snapshotGeneration;
        LoadSnapshot(generation);
    }

    private int ClearSnapshot()
    {
        lock (_gate)
        {
            _latest = null;
            return ++_snapshotGeneration;
        }
    }

    private void LoadSnapshot(int generation)
    {
        try
        {
            var snapshot = _loadSnapshot!();
            lock (_gate)
            {
                if (generation == _snapshotGeneration)
                    _latest = snapshot;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The old snapshot stays; the window falls back to reading the store itself when it has none.
            _logService?.LogError($"Statistics snapshot failed ({ex.GetType().Name}).");
        }
    }

    private void RaiseIndexCompleted()
    {
        // Each subscriber on its own: one that throws must neither skip the others nor end the thread.
        foreach (var handler in IndexCompleted?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logService?.LogError($"A statistics update handler failed ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>Stops the walk after whichever line it is currently on - the next <see
    /// cref="StartInBackground"/> call resumes from there, file granularity being the finest this
    /// class promises. Does not itself wait for the thread to actually exit; <see
    /// cref="Dispose"/> does, for at most five seconds.</summary>
    public void Cancel()
    {
        lock (_gate)
            _cancellation?.Cancel();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            _cancellation?.Cancel();
            thread = _thread;
        }

        // Bounded: the thread is a background thread, so the process can exit without it.
        if (thread is not null && !thread.Join(ShutdownWait))
            _logService?.LogError("Statistics indexing did not stop in time at shutdown.");
    }

    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
}
