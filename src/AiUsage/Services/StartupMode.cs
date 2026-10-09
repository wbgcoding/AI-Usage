using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// The one decision behind the first-start welcome window (<see cref="Views.WelcomeWindow"/>) -
/// pulled out of App.xaml.cs as a pure function so the rule itself is unit-testable without a live
/// Application.
/// </summary>
public static class StartupMode
{
    public static bool ShouldShowWelcome(AppSettings settings) => !settings.WelcomeShown;

    /// <summary>How long an autostart (<c>--tray</c>) waits before the window, tray icon, stats index and
    /// first fetches start, so the machine's logon is not slowed.</summary>
    public static readonly TimeSpan AutostartDelay = TimeSpan.FromSeconds(10);

    /// <summary>The wait before anything starts: <see cref="AutostartDelay"/> for the <c>--tray</c> start
    /// the Windows logon entry uses, none for every other start.</summary>
    public static TimeSpan StartDelay(IEnumerable<string> args) =>
        args.Contains("--tray", StringComparer.OrdinalIgnoreCase) ? AutostartDelay : TimeSpan.Zero;
}
