namespace AiUsage.Services;

/// <summary>
/// One running copy at a time: a second start brings the existing
/// window forward instead of opening twice. <c>--new-instance</c> opts out, but only in a Debug
/// build: two release copies would write one settings and history store (a deliberate second copy
/// uses <c>--second-instance</c>, which has its own data folder). The named-mutex ownership check
/// and the pure command-line decision are split out so the decision itself is testable without ever
/// touching a real OS mutex or window.
/// </summary>
public sealed class SingleInstanceService(
    string mutexName = SingleInstanceService.DefaultMutexName,
    string showEventName = SingleInstanceService.ShowEventName,
    string exitEventName = SingleInstanceService.ExitEventName) : IDisposable
{
    // "Local\", not "Global\": settings/history/logs already live per-user under %APPDATA%, so a
    // second Windows user (a different login, or another session over Remote Desktop) must be able
    // to run their own copy - a machine-wide "Global\" mutex would block them from starting at all.
    internal const string DefaultMutexName = "Local\\AI-Usage-SingleInstance-9F3B2C7A";
    // Separate name from the mutex above: the mutex decides ownership once at startup, this event
    // is signalled repeatedly (once per later start) and must never be confused with it.
    internal const string ShowEventName = "Local\\AI-Usage-Show-9F3B2C7A";
    // Signalled by Setup before it replaces the program file: the owner shuts down the ordinary way
    // (settings and index saved) instead of being killed. installer/AiUsage.iss names it verbatim.
    internal const string ExitEventName = "Local\\AI-Usage-Exit-9F3B2C7A";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _registeredWait;
    private RegisteredWaitHandle? _registeredExitWait;

    /// <summary>True when this process should proceed as the one and only instance.</summary>
    public bool AcquireOwnership(string[] args) => AcquireOwnership(args, NewInstanceAllowed,
        UpdateHost.IsAfterUpdateStart(args) ? AfterUpdateRetryWindow : TimeSpan.Zero);

    /// <summary>How long the copy a portable update starts keeps asking for the lock while the copy
    /// that started it is still shutting down.</summary>
    internal static readonly TimeSpan AfterUpdateRetryWindow = TimeSpan.FromSeconds(30);

    /// <summary>Asks for ownership again every <paramref name="step"/> until <paramref name="retryFor"/>
    /// has passed. A failed attempt gives its handle back first: a named mutex lives as long as any
    /// handle to it, so a kept handle would make the lock look taken after the old owner is gone.</summary>
    internal bool AcquireOwnership(string[] args, bool allowNewInstance, TimeSpan retryFor, TimeSpan? step = null)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (AcquireOwnership(args, allowNewInstance))
                return true;
            if (waited.Elapsed >= retryFor)
                return false;

            _mutex?.Dispose();
            _mutex = null;
            Thread.Sleep(step ?? TimeSpan.FromMilliseconds(250));
        }
    }

    internal bool AcquireOwnership(string[] args, bool allowNewInstance)
    {
        // A deliberate extra copy never listens on the shared show event: it is auto-reset, so a
        // later start would otherwise wake one running copy at random instead of the owner.
        if (WantsNewInstance(args, allowNewInstance))
            return true;

        try
        {
            _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew)
                CreateShowEvent();
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // Some other process already owns a same-named mutex under different privileges - the
            // single-instance check itself must never be why the app fails to start.
            TryCreateShowEvent();
            return true;
        }
    }

    /// <summary>The same access problem can hit the show event (a first copy running elevated owns it
    /// too): the event then stays null, a later start cannot wake this copy, and the start goes on.</summary>
    private void TryCreateShowEvent()
    {
        try
        {
            CreateShowEvent();
        }
        catch (UnauthorizedAccessException)
        {
            _showEvent?.Dispose();
            _showEvent = null;
            _exitEvent = null;
            LogService.Shared.LogInfo("Single instance: the show event belongs to a more privileged copy; a second start cannot raise this window.");
        }
    }

    /// <summary>Created here, right where ownership is decided, rather than later in <see
    /// cref="RegisterShowRequestHandler"/> - a second start's own <see cref="RequestShow"/> can land
    /// in the gap between this instance acquiring ownership and its main window (and the handler
    /// registered against this event) actually existing. Created for every path that returns true
    /// above, since every one of them is later followed by a <see cref="RegisterShowRequestHandler"/>
    /// call.</summary>
    private void CreateShowEvent()
    {
        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, showEventName);
        _exitEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, exitEventName);
    }

#if DEBUG
    internal const bool NewInstanceAllowed = true;
#else
    internal const bool NewInstanceAllowed = false;
#endif

    /// <summary>Whether the flag is honored in this build; false in every non-Debug build.</summary>
    internal static bool WantsNewInstance(string[] args) => WantsNewInstance(args, NewInstanceAllowed);

    /// <summary>Pulled out of <see cref="AcquireOwnership(string[])"/> so the flag-parsing itself is unit-testable.</summary>
    internal static bool WantsNewInstance(string[] args, bool allowed) =>
        allowed && args.Contains("--new-instance", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Called by the owning instance once it holds the mutex: wakes <paramref name="onShowRequested"/>
    /// (on a pool thread - the caller marshals to the UI thread itself) every time a later start
    /// signals <see cref="RequestShow"/>. A window title lookup cannot do this reliably: a WPF
    /// window hidden through <c>Window.Hide()</c> keeps its managed Visibility at Hidden, so a raw
    /// Win32 ShowWindow gets silently undone by WPF's own next layout pass.
    /// </summary>
    public void RegisterShowRequestHandler(Action onShowRequested)
    {
        if (_showEvent is null)
            return;
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Wakes <paramref name="onExitRequested"/> (on a pool thread) once Setup asks this copy
    /// to quit so it can replace the program file.</summary>
    public void RegisterExitRequestHandler(Action onExitRequested)
    {
        if (_exitEvent is null)
            return;
        _registeredExitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent, (_, _) => onExitRequested(), state: null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Called by a second instance that lost <see cref="AcquireOwnership(string[])"/>: asks the
    /// owning instance to bring its window forward. False when no owner could be signalled.</summary>
    public static bool RequestShow() => RequestShow(ShowEventName);

    /// <summary>Test seam: a per-test event name instead of the fixed production one, so a test
    /// signalling it never collides with a real running instance of the app or with another test
    /// running in parallel - the same reason the constructor's own mutex name is already
    /// overridable.</summary>
    internal static bool RequestShow(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var handle))
            return false;

        using (handle)
            return handle.Set();
    }

    /// <summary>The start of every ordinary copy: true when this process is the one instance, false
    /// when the running owner was asked to show its window.</summary>
    public bool AcquireOrRequestShow(string[] args) =>
        AcquireOrRequestShow(args, NewInstanceAllowed, () => RequestShow(), OwnerGoneRetryWindow);

    /// <summary>How long a start keeps asking for the lock when the owner it found left before it
    /// could be signalled.</summary>
    internal static readonly TimeSpan OwnerGoneRetryWindow = TimeSpan.FromSeconds(3);

    internal bool AcquireOrRequestShow(
        string[] args, bool allowNewInstance, Func<bool> requestShow, TimeSpan retryFor, TimeSpan? step = null)
    {
        var retry = UpdateHost.IsAfterUpdateStart(args) ? AfterUpdateRetryWindow : TimeSpan.Zero;
        if (AcquireOwnership(args, allowNewInstance, retry, step))
            return true;

        if (requestShow())
            return false;

        // No owner answered: it left between the failed lock and the signal. The failed attempt's
        // handle is given back first, or the lock would look taken for as long as it is held.
        _mutex?.Dispose();
        _mutex = null;
        return AcquireOwnership(args, allowNewInstance, retryFor, step);
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _registeredExitWait?.Unregister(null);
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        _mutex?.Dispose();
    }
}
