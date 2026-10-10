namespace AiUsage.Services;

/// <summary>
/// The single decision behind bringing a window forward: call the platform's own activation
/// once and stop there. A refused activation - Windows' own foreground-lock rule denying the
/// request, which shows up as a flashing taskbar button rather than a stolen focus - gets no retry,
/// no topmost toggle and no AttachThreadInput trick to force it through; that escalation would flash a
/// window that never asked for the interruption. Pulled out of
/// <c>MainWindow.ShowAndActivate</c> so this exact "never escalate a refusal" decision is provable
/// without a live window.
/// </summary>
public static class WindowActivation
{
    /// <summary>Calls <paramref name="activate"/> exactly once and returns whatever it reports -
    /// never a second attempt, whichever way the first one goes.</summary>
    public static bool TryActivate(Func<bool> activate) => activate();
}
