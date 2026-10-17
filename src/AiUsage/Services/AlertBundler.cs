using System.Windows.Threading;

namespace AiUsage.Services;

/// <summary>One toast-sized alert for an account; <see cref="Lines"/> are joined with a line break when shown.</summary>
public sealed record Alert(
    string AccountKey,
    string Title,
    IReadOnlyList<string> Lines,
    IReadOnlyList<(string Label, string Argument)> Buttons);

/// <summary>
/// Merges alerts of one account that arrive within <c>window</c> of the first into a single one, so one
/// fetch that crosses several limits shows one toast, not a stack of them. The first alert keeps the
/// title; lines are appended; buttons are united by argument (show, sign-in, mute, at most three).
/// UI thread only: <c>schedule</c> must call back on the thread that calls <see cref="Add"/>.
/// </summary>
public sealed class AlertBundler(
    TimeSpan window, Action<Alert> show, Func<TimeSpan, Action, IDisposable> schedule, Action<string>? log = null)
{
    private sealed class Pending(string title, IDisposable timer)
    {
        public string Title { get; } = title;
        public IDisposable Timer { get; } = timer;
        public List<string> Lines { get; } = [];
        public List<(string Label, string Argument)> Buttons { get; } = [];
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public void Add(Alert alert)
    {
        if (!_pending.TryGetValue(alert.AccountKey, out var bundle))
        {
            var key = alert.AccountKey;
            bundle = new Pending(alert.Title, schedule(window, () => Flush(key)));
            _pending[key] = bundle;
        }

        bundle.Lines.AddRange(alert.Lines);
        foreach (var button in alert.Buttons)
        {
            if (!bundle.Buttons.Exists(b => b.Argument == button.Argument))
                bundle.Buttons.Add(button);
        }
    }

    /// <summary>Shows every pending bundle now (shutdown).</summary>
    public void FlushAll()
    {
        foreach (var key in _pending.Keys.ToList())
            Flush(key);
    }

    private void Flush(string key)
    {
        if (!_pending.Remove(key, out var bundle))
            return;

        bundle.Timer.Dispose();
        var buttons = bundle.Buttons.OrderBy(b => Rank(b.Argument)).Take(ToastXml.MaxButtons).ToList();
        // Runs inside a dispatcher timer tick: a failing toast must not take the app down or keep
        // the other accounts' alerts from showing.
        try
        {
            show(new Alert(key, bundle.Title, bundle.Lines, buttons));
        }
        catch (Exception ex)
        {
            log?.Invoke($"An alert could not be shown ({ex.GetType().Name}): {PathSanitizer.Sanitize(ex.Message)}");
        }
    }

    private static int Rank(string argument) =>
        argument == ToastXml.ShowArgument ? 0
        : argument.StartsWith(ToastXml.SignInKind + ":", StringComparison.Ordinal) ? 1
        : argument.StartsWith(ToastXml.MuteKind + ":", StringComparison.Ordinal) ? 2
        : 3;

    /// <summary>Production schedule: a one-shot timer on the calling thread's dispatcher.</summary>
    public static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
