using System.Runtime.CompilerServices;

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
/// <remarks>
/// Only a direct user action (a click, a hotkey, a tray entry, a second start) may ask for the
/// foreground through <see cref="TryActivate"/>. Anything the app does on its own goes through
/// <see cref="TryActivateAutomatic"/>, which asks only when this process already holds the
/// foreground and so cannot be refused. Every request is written to the log, a given call site at most
/// once a minute, with the answer, so a flashing button can be traced to the caller.
/// </remarks>
public static class WindowActivation
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private static readonly object LogGate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastLogged = [];

    /// <summary>Where the lines go; a test replaces it.</summary>
    internal static Action<string> Log { get; set; } = line => LogService.Shared.LogInfo(line);

    /// <summary>The clock the once-a-minute rule reads; a test replaces it.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Whether this process holds the foreground; a test replaces it.</summary>
    internal static Func<bool> OwnProcessHoldsForeground { get; set; } = NativeOwnProcessHoldsForeground;

    /// <summary>Calls <paramref name="activate"/> exactly once and returns whatever it reports -
    /// never a second attempt, whichever way the first one goes. For a user action only.</summary>
    public static bool TryActivate(
        Func<bool> activate, [CallerFilePath] string callerFile = "", [CallerMemberName] string caller = "")
    {
        var accepted = activate();
        Record(CallSite(callerFile, caller), accepted ? "activated" : "refused by Windows");
        return accepted;
    }

    /// <summary>For anything the app does by itself (a sign-in that finished, a background refresh):
    /// asks for the foreground only while this process already has it, which Windows never refuses.
    /// Otherwise it does not ask at all, so no taskbar button can light up. False when it did not ask
    /// or was refused.</summary>
    public static bool TryActivateAutomatic(
        Func<bool> activate, [CallerFilePath] string callerFile = "", [CallerMemberName] string caller = "")
    {
        var site = CallSite(callerFile, caller);
        if (!OwnProcessHoldsForeground())
        {
            Record(site, "automatic, not asked (another program has the foreground)");
            return false;
        }

        var accepted = activate();
        Record(site, accepted ? "automatic, activated" : "automatic, refused by Windows");
        return accepted;
    }

    /// <summary>Notes that a window was shown, or built, by the app itself rather than by a user
    /// action, without asking for the foreground.</summary>
    public static void RecordAutomaticShow(string what) => Record(what, "shown without activation");

    /// <summary>Notes the outcome of a native foreground request made by the user (the tray menu).</summary>
    public static void RecordUserRequest(string what, bool accepted) =>
        Record(what, accepted ? "foreground taken" : "refused by Windows");

    private static string CallSite(string file, string member) =>
        $"{(string.IsNullOrEmpty(file) ? "?" : Path.GetFileNameWithoutExtension(file))}.{(member.Length == 0 ? "?" : member)}";

    private static void Record(string site, string outcome)
    {
        var key = $"{site}|{outcome}";
        var now = Clock();
        lock (LogGate)
        {
            if (LastLogged.TryGetValue(key, out var last) && now - last < LogInterval)
                return;
            LastLogged[key] = now;
        }

        Log($"Window activation: {site} {outcome}.");
    }

    /// <summary>Clears the once-a-minute memory, for a test.</summary>
    internal static void ResetLogMemory()
    {
        lock (LogGate)
            LastLogged.Clear();
    }

    /// <summary>One of this program's own windows is the active one, which means this process holds the
    /// foreground. Read on the UI thread, where every caller of the automatic path runs.</summary>
    private static bool NativeOwnProcessHoldsForeground()
    {
        var application = System.Windows.Application.Current;
        if (application is null || !application.Dispatcher.CheckAccess())
            return false;

        foreach (System.Windows.Window window in application.Windows)
        {
            if (window.IsActive)
                return true;
        }

        return false;
    }
}
