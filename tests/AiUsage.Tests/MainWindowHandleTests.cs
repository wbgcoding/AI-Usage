namespace AiUsage.Tests;

/// <summary>MainWindow is a live WPF window, so its start-up wiring is pinned from the source text.</summary>
public class MainWindowHandleTests
{
    private static readonly string Main = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.xaml.cs");
    private static readonly string Placement = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.Placement.cs");

    [Fact]
    public void The_constructor_creates_the_window_handle_so_a_tray_start_runs_the_handle_setup()
    {
        var ctorStart = Main.IndexOf("internal MainWindow(SettingsStore settingsStore, AppSettings? settings", StringComparison.Ordinal);
        Assert.True(ctorStart >= 0, "constructor not found");
        var ctor = Main[ctorStart..Main.IndexOf("\n    }", ctorStart, StringComparison.Ordinal)];

        Assert.Contains("SourceInitialized += MainWindow_SourceInitialized", ctor, StringComparison.Ordinal);
        Assert.Contains("new WindowInteropHelper(this).EnsureHandle()", ctor, StringComparison.Ordinal);
        Assert.True(
            ctor.IndexOf("SourceInitialized += MainWindow_SourceInitialized", StringComparison.Ordinal)
                < ctor.IndexOf("EnsureHandle()", StringComparison.Ordinal),
            "the handle must be created after the SourceInitialized subscription");
    }

    [Fact]
    public void The_display_settings_handler_marshals_to_the_dispatcher()
    {
        Assert.Matches(
            @"SystemEvents_DisplaySettingsChanged\(object\? sender, EventArgs e\) =>\s+Dispatcher\.BeginInvoke\(ReplaceOnScreen\);",
            Placement);
    }

    [Fact]
    public void The_user_preference_handler_marshals_to_the_dispatcher()
    {
        var body = SourceScan.Member(Main, "private void SystemEvents_UserPreferenceChanged");
        var apply = body.IndexOf("ThemeService.Apply", StringComparison.Ordinal);
        var marshal = body.IndexOf("Dispatcher.BeginInvoke", StringComparison.Ordinal);
        Assert.True(marshal >= 0 && apply > marshal, "ThemeService.Apply must run inside Dispatcher.BeginInvoke");
    }
}
