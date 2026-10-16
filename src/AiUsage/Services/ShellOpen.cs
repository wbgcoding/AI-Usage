using System.ComponentModel;
using System.Diagnostics;

namespace AiUsage.Services;

/// <summary>Hands a URL or folder to the Windows shell. Best-effort: no registered handler, a missing
/// folder or a denied access leave nothing sensible to recover into, so a failure is swallowed.</summary>
internal static class ShellOpen
{
    internal static void TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // No handler for it, or the target is not reachable.
        }
    }

    /// <summary>Creates <paramref name="folder"/> when it is missing, then opens it.</summary>
    internal static void TryOpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        TryOpen(folder);
    }
}
