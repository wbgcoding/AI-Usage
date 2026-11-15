using System.Windows.Threading;

namespace AiUsage.ViewModels;

/// <summary>Runs work on the UI thread: inline when the caller already is on it or the app has no
/// dispatcher (tests, shutdown), queued otherwise.</summary>
internal static class UiThread
{
    internal static void Run(Action action) => Run(Application.Current?.Dispatcher, action);

    internal static void Run(Dispatcher? dispatcher, Action action)
    {
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
