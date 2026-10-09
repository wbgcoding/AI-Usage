using System.Windows;
using System.Windows.Input;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;

namespace AiUsage.Views;

/// <summary>
/// Shown once, right after the very first start on a fresh profile (see
/// <see cref="StartupMode.ShouldShowWelcome"/>) - before the window fills with placeholder tiles,
/// this explains what the widget does and offers a direct path to sign in. The provider rows bind
/// straight to MainWindow's own tile view models and their existing RunAction/SignOut commands
/// (already wired to the real sign-in flow in MainWindow's constructor), so there is no second
/// sign-in path to keep working. Has no <see cref="Models.AppSettings"/> reference of its own - the
/// opacity mode comes from <see cref="WindowOpacity.CurrentPercent"/>,
/// already synced by App.xaml.cs before this window is ever constructed.
/// </summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow(IEnumerable<ProviderTileViewModel> tiles)
    {
        InitializeComponent();
        WindowChromeNative.Bootstrap(this);
        TitleBarControl.TitleText = LocalizationService.Instance["Welcome.Title"];
        ProviderList.ItemsSource = tiles.Select(tile => new WelcomeRow(tile)).ToList();

        // Esc closes dialogs, keyboard-only throughout - same contract as every other window built
        // on this chrome (ConfirmWindow, CrashWindow).
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += WelcomeWindow_Loaded;
    }

    /// <summary>Centres on whichever monitor the mouse is on right now - the same recovery-position
    /// logic MainWindow's own "bring window back" tray entry uses. Done on Loaded rather than in the
    /// constructor: with a fixed width and SizeToContent="Height", ActualHeight only reflects the
    /// real, wrapped text height once the first layout pass has run.</summary>
    private void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var (cursorX, cursorY) = NativeMonitors.CursorPositionInWorkAreaUnits();
        var monitors = NativeMonitors.WorkAreas();
        var primary = monitors.Count > 0 ? monitors[0] : default;

        var resolved = WindowPlacementService.CenterOnCursorMonitor(cursorX, cursorY, ActualWidth, ActualHeight, monitors, primary);
        Left = resolved.Left;
        Top = resolved.Top;
    }

    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => Close();

    private void StartButton_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>One provider row of the welcome window: the tile itself plus what only this window adds,
/// the "found" mark for a provider that already reads and a muted hint for the two that depend on
/// another tool. Re-raises its own flags whenever the tile's status or sign-in state moves, so the
/// row flips to "Found" the moment a sign-in started from here succeeds.</summary>
public sealed class WelcomeRow : System.ComponentModel.INotifyPropertyChanged
{
    public WelcomeRow(ProviderTileViewModel tile)
    {
        Tile = tile;
        Hint = HintFor(tile.RealProviderId);
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProviderTileViewModel.Status) or nameof(ProviderTileViewModel.ShowSignIn))
            {
                PropertyChanged?.Invoke(this, new(nameof(IsFound)));
                PropertyChanged?.Invoke(this, new(nameof(ShowSignIn)));
            }
        };
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ProviderTileViewModel Tile { get; }

    /// <summary>The provider already delivers numbers (fresh or merely out of date).</summary>
    public bool IsFound => Tile.Status is ProviderStatus.Ok or ProviderStatus.Stale;

    public bool ShowSignIn => !IsFound && Tile.ShowSignIn;

    public string? Hint { get; }

    public bool HasHint => Hint is not null;

    /// <summary>The muted extra line for a provider that depends on another tool, null for the rest.</summary>
    public static string? HintFor(string providerId) => providerId switch
    {
        "gemini" => LocalizationService.Instance["Welcome.GeminiHint"],
        "copilot" => LocalizationService.Instance["Welcome.CopilotHint"],
        _ => null,
    };
}
