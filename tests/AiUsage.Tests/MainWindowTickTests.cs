using AiUsage.ViewModels;
using AiUsage.Views;

namespace AiUsage.Tests;

/// <summary>
/// MainWindow itself cannot be unit-instantiated (a live WPF Window, same reasoning
/// MainWindowCloseGuardTests documents), so the tick handler's own invariant - it no longer
/// recomputes the tile density itself, that now runs only from the inputs that can actually change
/// it - is proven the same way: <see cref="MainWindow.ResolveTickInterval"/> is a pure predicate,
/// directly tested, and the "not from the tick" half is proven by scanning the source text.
/// </summary>
public class MainWindowTickTests
{
    [Fact]
    public void ResolveTickInterval_is_one_second_visible_and_thirty_seconds_otherwise()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), MainWindow.ResolveTickInterval(windowVisible: true));
        Assert.Equal(TimeSpan.FromSeconds(30), MainWindow.ResolveTickInterval(windowVisible: false));
    }

    [Fact]
    public void The_tick_handler_no_longer_calls_UpdateDensity_and_the_tile_count_is_wired_separately()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        var tickStart = source.IndexOf("_tickTimer.Tick +=", StringComparison.Ordinal);
        Assert.True(tickStart >= 0, "_tickTimer.Tick += not found");
        var tickEnd = source.IndexOf("_tickTimer.Start();", tickStart, StringComparison.Ordinal);
        Assert.True(tickEnd >= 0, "_tickTimer.Start() not found after the Tick handler");
        var tickBody = source.Substring(tickStart, tickEnd - tickStart);

        Assert.DoesNotContain("UpdateDensity()", tickBody, StringComparison.Ordinal);

        Assert.Contains("Tiles.CollectionChanged += ViewModel_Tiles_CollectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("Tiles.CollectionChanged -= ViewModel_Tiles_CollectionChanged", source, StringComparison.Ordinal);
        var handlerStart = source.IndexOf("private void ViewModel_Tiles_CollectionChanged", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "ViewModel_Tiles_CollectionChanged handler not found");
        var handlerBody = source.Substring(handlerStart, Math.Min(200, source.Length - handlerStart));
        Assert.Contains("UpdateDensity()", handlerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowAndActivate_resets_the_tick_interval_instead_of_waiting_for_the_next_tick()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        var methodStart = source.IndexOf("public void ShowAndActivate()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ShowAndActivate() not found");
        var methodEnd = source.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd >= 0, "End of ShowAndActivate() not found");
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        Assert.Contains("_tickTimer.Interval = ResolveTickInterval(windowVisible: true)", methodBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void IsAttended_needs_a_visible_window_on_an_unlocked_session(bool windowVisible, bool sessionLocked, bool expected) =>
        Assert.Equal(expected, MainViewModel.IsAttended(windowVisible, sessionLocked));

    [Fact]
    public void The_window_follows_session_lock_and_power_events_and_lets_go_of_them_on_dispose()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("SystemEvents.SessionSwitch += SystemEvents_SessionSwitch", source, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged", source, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch", source, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged", source, StringComparison.Ordinal);
        Assert.Contains("MainViewModel.IsAttended(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Suspended", source, StringComparison.Ordinal); // no code runs asleep; a missed resume would stick
    }

    [Fact]
    public void Coming_back_runs_the_normal_due_check_not_a_forced_refresh()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        var start = source.IndexOf("private void SystemEvents_SessionSwitch", StringComparison.Ordinal);
        var end = source.IndexOf("private void MainWindow_SourceInitialized", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var region = source.Substring(start, end - start);

        // A forced refresh would bypass every provider's own minimum interval.
        Assert.DoesNotContain("RefreshNow", region, StringComparison.Ordinal);
        Assert.Contains("ViewModel.Tick(", region, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refresh_scheduler_is_fed_the_attended_state_not_just_window_visibility()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("_scheduler.Tick(IsTileHidden, IsAttended(WindowVisible, SessionLocked)", source, StringComparison.Ordinal);
        Assert.Contains("_scheduler.RefreshNow(IsTileHidden, IsAttended(WindowVisible, SessionLocked)", source, StringComparison.Ordinal);
    }

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }
}
