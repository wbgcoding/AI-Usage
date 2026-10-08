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
}
