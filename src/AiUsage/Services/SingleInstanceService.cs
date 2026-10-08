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
    string showEventName = SingleInstanceService.ShowEventName) : IDisposable
{
    // "Local\", not "Global\": settings/history/logs already live per-user under %APPDATA%, so a
    // second Windows user (a different login, or another session over Remote Desktop) must be able
    // to run their own copy - a machine-wide "Global\" mutex would block them from starting at all.
    internal const string DefaultMutexName = "Local\\AI-Usage-SingleInstance-9F3B2C7A";
    // Separate name from the mutex above: the mutex decides ownership once at startup, this event
    // is signalled repeatedly (once per later start) and must never be confused with it.
    internal const string ShowEventName = "Local\\AI-Usage-Show-9F3B2C7A";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registeredWait;

    /// <summary>True when this process should proceed as the one and only instance.</summary>
    public bool AcquireOwnership(string[] args) => AcquireOwnership(args, NewInstanceAllowed);

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
            CreateShowEvent();
            return true;
        }
    }

    /// <summary>Created here, right where ownership is decided, rather than later in <see
    /// cref="RegisterShowRequestHandler"/> - a second start's own <see cref="RequestShow"/> can land
    /// in the gap between this instance acquiring ownership and its main window (and the handler
    /// registered against this event) actually existing. Created for every path that returns true
    /// above, since every one of them is later followed by a <see cref="RegisterShowRequestHandler"/>
    /// call.</summary>
    private void CreateShowEvent() =>
        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, showEventName);

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

    /// <summary>Called by a second instance that lost <see cref="AcquireOwnership"/>: asks the
    /// owning instance to bring its window forward, then this instance exits.</summary>
    public static void RequestShow() => RequestShow(ShowEventName);

    /// <summary>Test seam: a per-test event name instead of the fixed production one, so a test
    /// signalling it never collides with a real running instance of the app or with another test
    /// running in parallel - the same reason the constructor's own mutex name is already
    /// overridable.</summary>
    internal static void RequestShow(string eventName)
    {
        if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
        {
            using var _ = handle;
            handle.Set();
        }
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _showEvent?.Dispose();
        _mutex?.Dispose();
    }
}
