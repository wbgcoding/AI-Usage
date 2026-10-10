namespace AiUsage.Services;

/// <summary>
/// Remembers the last tray tooltip text so MainWindow's once-a-second tick only sets the tray icon's
/// tooltip (a Win32 call) when the rendered text actually changed.
/// </summary>
internal sealed class TrayTooltipMemo
{
    private string? _lastText;

    /// <summary>Returns true (and remembers <paramref name="text"/>) the first time it differs from
    /// the previous call; returns false for a repeat of the same text.</summary>
    public bool HasChanged(string text)
    {
        if (text == _lastText)
            return false;

        _lastText = text;
        return true;
    }
}
