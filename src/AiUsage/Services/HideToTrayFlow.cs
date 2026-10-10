namespace AiUsage.Services;

/// <summary>
/// The shared "close the window into the tray" body used both by the title bar's close button and
/// by a cancelled window Closing event (Alt+F4, taskbar close) - one flow so the two paths can never
/// drift apart again. Pulled out as injectable delegates so it is unit-testable without a live
/// window or a real tray icon.
/// </summary>
internal static class HideToTrayFlow
{
    /// <summary>Hides the window, then shows the "still running" hint the first time only. Returns
    /// true when the hint fired.</summary>
    internal static bool Run(bool hintAlreadyShown, Action hide, Action showBalloon, Action markHintShown)
    {
        hide();

        if (!TrayHintPolicy.ShouldShowHint(hintAlreadyShown))
            return false;

        markHintShown();
        showBalloon();
        return true;
    }
}
