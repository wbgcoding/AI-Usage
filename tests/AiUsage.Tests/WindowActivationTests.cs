using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// The pure decision behind <see cref="WindowActivation.TryActivate"/>: a refused activation
/// never gets a second attempt. Both facts below spy on the call count to prove that directly -
/// "does not escalate" means the underlying activation function is called exactly once, no matter
/// which way it answers.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class WindowActivationTests : IDisposable
{
    private readonly Action<string> _log = WindowActivation.Log;
    private readonly Func<DateTimeOffset> _clock = WindowActivation.Clock;
    private readonly Func<bool> _foreground = WindowActivation.OwnProcessHoldsForeground;
    private readonly List<string> _lines = [];
    private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public WindowActivationTests()
    {
        WindowActivation.ResetLogMemory();
        WindowActivation.Log = _lines.Add;
        WindowActivation.Clock = () => _now;
    }

    public void Dispose()
    {
        WindowActivation.Log = _log;
        WindowActivation.Clock = _clock;
        WindowActivation.OwnProcessHoldsForeground = _foreground;
        WindowActivation.ResetLogMemory();
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot.Find(), "src", "AiUsage", .. parts]));

    [Fact]
    public void A_successful_activation_calls_activate_exactly_once()
    {
        var callCount = 0;
        bool Activate()
        {
            callCount++;
            return true;
        }

        var result = WindowActivation.TryActivate(Activate);

        Assert.True(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void A_refused_activation_calls_activate_exactly_once_and_does_not_escalate()
    {
        var callCount = 0;
        bool Activate()
        {
            callCount++;
            return false;
        }

        var result = WindowActivation.TryActivate(Activate);

        Assert.False(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void An_automatic_request_never_activates_while_another_program_has_the_foreground()
    {
        WindowActivation.OwnProcessHoldsForeground = () => false;
        var calls = 0;

        var result = WindowActivation.TryActivateAutomatic(() =>
        {
            calls++;
            return true;
        });

        Assert.False(result);
        Assert.Equal(0, calls);
        Assert.Contains("not asked", Assert.Single(_lines));
    }

    [Fact]
    public void An_automatic_request_asks_once_while_this_program_has_the_foreground()
    {
        WindowActivation.OwnProcessHoldsForeground = () => true;
        var calls = 0;

        var result = WindowActivation.TryActivateAutomatic(() =>
        {
            calls++;
            return true;
        });

        Assert.True(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_request_is_logged_with_its_caller_and_the_answer()
    {
        WindowActivation.TryActivate(() => false);

        var line = Assert.Single(_lines);
        Assert.Contains(nameof(WindowActivationTests), line);
        Assert.Contains(nameof(A_request_is_logged_with_its_caller_and_the_answer), line);
        Assert.Contains("refused by Windows", line);
    }

    [Fact]
    public void A_call_site_is_logged_once_a_minute()
    {
        for (var i = 0; i < 5; i++)
            WindowActivation.TryActivate(() => true);
        Assert.Single(_lines);

        _now += TimeSpan.FromSeconds(59);
        WindowActivation.TryActivate(() => true);
        Assert.Single(_lines);

        _now += TimeSpan.FromSeconds(2);
        WindowActivation.TryActivate(() => true);
        Assert.Equal(2, _lines.Count);
    }

    [Fact]
    public void An_automatic_show_is_logged_by_what_it_was()
    {
        WindowActivation.RecordAutomaticShow("MainWindow fullscreen return");

        Assert.Contains("MainWindow fullscreen return shown without activation", Assert.Single(_lines));
    }

    [Fact]
    public void The_program_never_flashes_a_window()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot.Find(), "src", "AiUsage"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("FlashWindow", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    // The automatic paths are checked in the source: a live main window cannot be built in a test.
    [Fact]
    public void The_sign_in_completion_shows_the_widget_without_activating_it()
    {
        var source = Source("Views", "MainWindow.xaml.cs");

        Assert.Matches(@"CompleteSignIn\(accountKey, signedIn\);[^}]*?if \(!IsWelcomeOpen\(\)\)\s*ShowAndActivate\(userInitiated: false\);", source);
    }

    [Fact]
    public void The_automatic_branch_of_the_show_never_activates_on_its_own()
    {
        var source = Source("Views", "MainWindow.xaml.cs");
        var start = source.IndexOf("private void ShowAndActivate(bool userInitiated)", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("/// <summary>The tray menu's recovery entry", start, StringComparison.Ordinal)];

        Assert.Contains("ShowActivated = false", body);
        Assert.Contains("TryActivateAutomatic(Activate)", body);
        // The one plain request belongs to the user branch.
        Assert.Equal(2, body.Split("TryActivate(Activate)").Length);
        Assert.DoesNotContain(".Activate()", body);
    }

    [Fact]
    public void The_refresh_after_a_show_from_the_tray_waits_for_the_first_frame()
    {
        var source = Source("Views", "MainWindow.xaml.cs");
        var start = source.IndexOf("private void ShowAndActivate(bool userInitiated)", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("/// <summary>The tray menu's recovery entry", start, StringComparison.Ordinal)];

        // Before: "ViewModel.RefreshNow();" ran synchronously, ahead of the window's own render.
        Assert.Contains("Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ViewModel.RefreshNow()));", body);
        Assert.DoesNotContain("            ViewModel.RefreshNow();", body);
    }

    [Fact]
    public void The_fullscreen_return_and_the_prebuild_never_activate()
    {
        var main = Source("Views", "MainWindow.xaml.cs");
        var fullscreen = main[main.IndexOf("private void FullscreenWatcher_FullscreenChanged", StringComparison.Ordinal)..];
        fullscreen = fullscreen[..fullscreen.IndexOf("private void ApplyWindowLayer", StringComparison.Ordinal)];
        Assert.Contains("ShowNativeNoActivate", fullscreen);
        Assert.DoesNotContain("Activate(", fullscreen.Replace("ShowNativeNoActivate", ""));

        var stats = Source("Views", "StatsWindow.xaml.cs");
        var prepare = stats[stats.IndexOf("internal void PrepareOffscreen()", StringComparison.Ordinal)..];
        prepare = prepare[..prepare.IndexOf("private long _openStartedAt", StringComparison.Ordinal)];
        Assert.DoesNotContain("Show(", prepare);
        Assert.DoesNotContain("Activate(", prepare);
    }
}
