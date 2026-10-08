namespace AiUsage.Services;

/// <summary>
/// Whether the one-time "still running in the tray" balloon should fire, pulled out of the
/// window's close handler as a pure function so the decision is testable without a real window.
/// </summary>
public static class TrayHintPolicy
{
    public static bool ShouldShowHint(bool alreadyShown) => !alreadyShown;
}
