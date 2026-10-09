using System.Security;
using Microsoft.Win32;

namespace AiUsage.Services;

/// <summary>
/// Autostart via the one standard, per-user Run key - never a
/// scheduled task, never HKLM. The registry itself is the single source of truth: nothing here
/// reads or writes <see cref="Models.AppSettings"/>, so the Settings window's checkbox always
/// reflects what actually runs at logon, not a remembered wish that could drift from it. That
/// includes Task Manager's Startup tab, which does not touch the Run value: it flips a marker under
/// the StartupApproved key, and a disabled entry does not run at logon.
/// </summary>
public static class AutostartService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "AI-Usage";

    public static bool IsEnabled() => IsEnabled(RunKeyPath, StartupApprovedKeyPath);

    /// <summary>False when Windows refused the write (locked-down policy, missing permission) -
    /// never throws, so a settings checkbox toggle never takes the whole app down with it.</summary>
    public static bool Enable(string exePath) => Enable(RunKeyPath, exePath, StartupApprovedKeyPath);

    /// <summary>Same "never throws, report failure instead" contract as <see cref="Enable(string)"/>.</summary>
    public static bool Disable() => Disable(RunKeyPath);

    /// <summary>Test seam: throwaway keys under HKCU instead of the real Run and StartupApproved
    /// keys. On when the Run value starts this very program (an entry for another path, say a copy
    /// that was moved, is not this copy's autostart) and Task Manager has not disabled it: its marker
    /// is a binary value whose first byte is odd for a disabled entry (0x03) and even for an enabled
    /// one (0x02, 0x06); no marker, or one of another shape, counts as enabled.</summary>
    internal static bool IsEnabled(string keyPath, string? approvedKeyPath = null, string? currentExePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var exe = currentExePath ?? Environment.ProcessPath;
            if (key?.GetValue(ValueName) is not string value || exe is null || !SamePath(ExtractExePath(value), exe))
                return false;

            if (approvedKeyPath is null)
                return true;

            using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } marker || (marker[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Writes `"&lt;exePath&gt;" --tray` - `--tray` (App.xaml.cs) skips showing the main
    /// window while every background service (tray icon, polling, notifications) still starts.
    /// Ticking the box in this app is an explicit choice, so a Task Manager disable marker is
    /// removed too; otherwise the entry would stay off.</summary>
    internal static bool Enable(string keyPath, string exePath, string? approvedKeyPath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(ValueName, RunValue(exePath));

            if (approvedKeyPath is not null)
            {
                using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Called once at startup when autostart is on: an entry that still names a program file
    /// which no longer exists (the copy was moved or reinstalled elsewhere) is pointed at this copy.
    /// An entry for another copy that still exists is left alone, as is a missing entry (the user
    /// removed it) and the Task Manager marker. Never throws; true when the value was rewritten.</summary>
    public static bool RepairIfMoved(string exePath) => RepairIfMoved(RunKeyPath, exePath, File.Exists);

    internal static bool RepairIfMoved(string keyPath, string exePath, Func<string, bool> exists)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value)
                return false;

            var recorded = ExtractExePath(value);
            if (SamePath(recorded, exePath) || (recorded is not null && exists(recorded)))
                return false;

            key.SetValue(ValueName, RunValue(exePath));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static string RunValue(string exePath) => $"\"{exePath}\" --tray";

    /// <summary>The program path of a Run value: the quoted part, or the text before the first
    /// switch when it is not quoted.</summary>
    internal static string? ExtractExePath(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var switchStart = text.IndexOf(" --", StringComparison.Ordinal);
        return switchStart > 0 ? text[..switchStart] : text.Length > 0 ? text : null;
    }

    private static bool SamePath(string? recorded, string exePath) =>
        recorded is not null && string.Equals(recorded, exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the value entirely rather than setting it empty -
    /// never throws if it was already gone, so a double-disable is always safe.</summary>
    internal static bool Disable(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
