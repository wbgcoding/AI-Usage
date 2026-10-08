using AiUsage;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// Pure test of the one decision behind the first-start welcome window - no live Application or
/// window is ever needed to prove this rule.
/// </summary>
public class AppStartupTests
{
    [Fact]
    public void ShouldShowWelcome_is_true_on_a_fresh_profile()
    {
        Assert.True(StartupMode.ShouldShowWelcome(new AppSettings()));
    }

    [Fact]
    public void ShouldShowWelcome_is_false_once_the_welcome_window_has_already_shown()
    {
        var settings = new AppSettings { WelcomeShown = true };

        Assert.False(StartupMode.ShouldShowWelcome(settings));
    }

    // The crash this guards against: a crash mid-startup (e.g. during the modal welcome dialog) can
    // already have run Shutdown() by the time OnStartup gets back to showing the main window - Show()
    // on a window WPF has already closed as part of that shutdown throws InvalidOperationException
    // instead of quietly doing nothing, so a closed main window must never be shown.
    [Fact]
    public void ShouldShowMainWindow_is_false_once_shutdown_has_already_started()
    {
        Assert.False(App.ShouldShowMainWindow(dispatcherHasShutdownStarted: true));
    }

    [Fact]
    public void ShouldShowMainWindow_is_true_during_an_ordinary_start()
    {
        Assert.True(App.ShouldShowMainWindow(dispatcherHasShutdownStarted: false));
    }
}
