using System.Runtime.InteropServices;
using System.Windows;

namespace AiUsage.Services;

/// <summary>
/// The Windows clipboard is a shared, lockable resource - another process holding it open for a
/// moment is common enough that a single silent retry is worth it before giving up.
/// </summary>
internal static class ClipboardHelper
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    internal static void SetTextSafely(string text) => SetTextSafely(text, Clipboard.SetText);

    /// <summary>Test seam: an injected write action instead of the real Clipboard.</summary>
    internal static void SetTextSafely(string text, Action<string> setText)
    {
        try
        {
            setText(text);
        }
        catch (Exception ex) when (ex is ExternalException)
        {
            Thread.Sleep(RetryDelay);
            try
            {
                setText(text);
            }
            catch (Exception ex2) when (ex2 is ExternalException)
            {
                // Still locked after one retry - nothing sensible to recover into.
            }
        }
    }
}
