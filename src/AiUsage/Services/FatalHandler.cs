namespace AiUsage.Services;

/// <summary>
/// The pure "which thread am I on" decision behind App.xaml.cs's HandleFatal, pulled out so it is
/// unit-testable without a live WPF Application: AppDomain.UnhandledException can fire on any
/// thread, and creating a Window off the UI thread throws InvalidOperationException, which used to
/// mean the crash dialog silently never appeared for exactly the class of crash it exists for.
/// </summary>
internal static class FatalHandler
{
    /// <summary>Runs <paramref name="showDialog"/> directly when already on the target thread,
    /// otherwise marshals it through <paramref name="invoke"/> exactly once. When
    /// <paramref name="isShuttingDown"/> answers true, neither happens at all: a crash reached while
    /// the application is already tearing itself down (e.g. a second, unrelated exception during an
    /// earlier crash's own Shutdown()) must never be the thing that throws a second time by building
    /// a window the shutting-down dispatcher can no longer host - <paramref name="logShuttingDown"/>
    /// runs instead, so the crash is still on record even though no dialog appears.</summary>
    /// <summary>Runs <paramref name="action"/> on <paramref name="dispatcher"/> and waits for it to
    /// finish, but only when the UI thread picks it up within <paramref name="startTimeout"/>. A hung
    /// UI thread would otherwise block the failing background thread forever; false means the action
    /// never ran (it is aborted). Once started, the action may take as long as it needs - a crash
    /// dialog stays open until the user closes it.</summary>
    internal static bool InvokeWhenResponsive(System.Windows.Threading.Dispatcher dispatcher, Action action, TimeSpan startTimeout)
    {
        using var started = new ManualResetEventSlim();
        var operation = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () =>
        {
            started.Set();
            action();
        });

        if (!started.Wait(startTimeout))
        {
            operation.Abort();
            // Aborting loses against a start that landed just now; the action then runs and is waited for.
            if (operation.Status != System.Windows.Threading.DispatcherOperationStatus.Aborted)
            {
                operation.Wait();
                return true;
            }

            return false;
        }

        operation.Wait();
        return true;
    }

    internal static void Show(Action showDialog, Func<bool> checkAccess, Action<Action> invoke, Func<bool> isShuttingDown, Action logShuttingDown)
    {
        if (isShuttingDown())
        {
            logShuttingDown();
            return;
        }

        if (checkAccess())
            showDialog();
        else
            invoke(showDialog);
    }
}
