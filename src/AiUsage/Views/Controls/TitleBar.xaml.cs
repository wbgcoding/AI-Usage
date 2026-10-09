using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Services;

namespace AiUsage.Views.Controls;

/// <summary>
/// The window's own title bar: dragging, double-click-to-collapse, the action buttons and the window menu.
/// Raises events instead of acting on the window itself, so MainWindow (or a test) decides what
/// "refresh", "settings", "minimize" and "close" actually do.
/// </summary>
public partial class TitleBar : UserControl
{
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? StatsRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? CollapseToggleRequested;
    public event EventHandler<bool>? AlwaysOnTopToggleRequested;

    /// <summary>A press outside a StaysOpen=False popup closes it before the button's click arrives,
    /// so a click shortly after a close is the second click of a toggle and must not reopen it.</summary>
    private const long EyeReopenGuardMs = 300;

    private long _eyeClosedAtTicks = long.MinValue / 2;
    private bool _eyeOpenedByKeyboard;

    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(nameof(TitleText), typeof(string), typeof(TitleBar), new PropertyMetadata("AI-Usage"));

    public static readonly DependencyProperty ShowEyeMenuProperty =
        DependencyProperty.Register(nameof(ShowEyeMenu), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    /// <summary>Whether the window menu offers "Refresh" (and the separator after it).</summary>
    public static readonly DependencyProperty ShowRefreshProperty =
        DependencyProperty.Register(nameof(ShowRefresh), typeof(bool), typeof(TitleBar),
            new PropertyMetadata(true, (d, _) => ((TitleBar)d).ApplyRefreshMenuVisibility()));

    public static readonly DependencyProperty ShowSettingsProperty =
        DependencyProperty.Register(nameof(ShowSettings), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    /// <summary>Opt-in, unlike <see cref="ShowSettings"/>: only the main widget's own title bar sets
    /// this to true - every dialog built on this same control leaves it at its default of
    /// false rather than needing its own explicit override, the reverse convention from the other
    /// Show* flags above.</summary>
    public static readonly DependencyProperty ShowStatsProperty =
        DependencyProperty.Register(nameof(ShowStats), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowMinimizeProperty =
        DependencyProperty.Register(nameof(ShowMinimize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    /// <summary>Only the main widget collapses to its title bar; a dialog just ignores the second click.</summary>
    public static readonly DependencyProperty AllowCollapseProperty =
        DependencyProperty.Register(nameof(AllowCollapse), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    /// <summary>Set by MainWindow from its own <c>_settings.Window.Collapsed</c> - this control must
    /// keep working with a <c>SettingsViewModel</c> DataContext, so it never binds this from its own
    /// inherited DataContext.</summary>
    public static readonly DependencyProperty IsCollapsedProperty =
        DependencyProperty.Register(nameof(IsCollapsed), typeof(bool), typeof(TitleBar),
            new PropertyMetadata(false, (d, _) => ((TitleBar)d).RefreshWindowMenuCollapseHeader()));

    /// <summary>Only the main widget gets the window menu (right-click or Alt+Space) that restores
    /// window-manager behaviour lost to <c>AllowsTransparency</c> - every dialog built on this same
    /// control leaves it at its default of False, same opt-in convention as the other Show* flags.</summary>
    public static readonly DependencyProperty ShowWindowMenuProperty =
        DependencyProperty.Register(nameof(ShowWindowMenu), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    /// <summary>The eye popup's badge count and accessible name read <c>HiddenCount</c> and
    /// <c>EyeButtonAccessibleName</c> off this property instead of the inherited DataContext:
    /// <c>SettingsWindow</c> reuses this control with a <c>SettingsViewModel</c> DataContext that has
    /// neither member, which produced a binding error on every Settings-window open. Set from
    /// <c>MainWindow.xaml</c> only (<c>EyeSource="{Binding}"</c>); <c>SettingsWindow</c> leaves it
    /// null and the eye button stays hidden there anyway (<see cref="ShowEyeMenu"/>).</summary>
    public static readonly DependencyProperty EyeSourceProperty =
        DependencyProperty.Register(nameof(EyeSource), typeof(object), typeof(TitleBar), new PropertyMetadata(null));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public bool ShowEyeMenu
    {
        get => (bool)GetValue(ShowEyeMenuProperty);
        set => SetValue(ShowEyeMenuProperty, value);
    }

    public bool ShowRefresh
    {
        get => (bool)GetValue(ShowRefreshProperty);
        set => SetValue(ShowRefreshProperty, value);
    }

    public bool ShowSettings
    {
        get => (bool)GetValue(ShowSettingsProperty);
        set => SetValue(ShowSettingsProperty, value);
    }

    public bool ShowStats
    {
        get => (bool)GetValue(ShowStatsProperty);
        set => SetValue(ShowStatsProperty, value);
    }

    public bool ShowMinimize
    {
        get => (bool)GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool AllowCollapse
    {
        get => (bool)GetValue(AllowCollapseProperty);
        set => SetValue(AllowCollapseProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    public bool ShowWindowMenu
    {
        get => (bool)GetValue(ShowWindowMenuProperty);
        set => SetValue(ShowWindowMenuProperty, value);
    }

    public object? EyeSource
    {
        get => GetValue(EyeSourceProperty);
        set => SetValue(EyeSourceProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
        RefreshWindowMenuCollapseHeader();
        // The collapse entry's header is set from code (a context menu cannot reach IsCollapsed
        // through a RelativeSource binding), so it has to follow a language switch by hand. A weak
        // subscription, so the long-lived localization service never keeps a closed dialog alive.
        PropertyChangedEventManager.AddHandler(LocalizationService.Instance, Localization_PropertyChanged, "Item[]");
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The service is shared; a language switch raised on another thread reaches this bar on its own.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshWindowMenuCollapseHeader);
            return;
        }

        RefreshWindowMenuCollapseHeader();
    }

    private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (AllowCollapse)
                CollapseToggleRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            Window.GetWindow(this)?.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button was already released by the time DragMove ran (fast click, or
            // another window stole capture); WPF throws instead of just no-op-ing.
        }
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyRefreshMenuVisibility()
    {
        if (RefreshMenuItem is null)
            return;
        var visibility = ShowRefresh ? Visibility.Visible : Visibility.Collapsed;
        RefreshMenuItem.Visibility = visibility;
        RefreshMenuSeparator.Visibility = visibility;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void StatsButton_Click(object sender, RoutedEventArgs e) => StatsRequested?.Invoke(this, EventArgs.Empty);

    private void CollapseMenuItem_Click(object sender, RoutedEventArgs e) => CollapseToggleRequested?.Invoke(this, EventArgs.Empty);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Refuses the window menu entirely for every window that leaves
    /// <see cref="ShowWindowMenu"/> at its default - a dialog built on this same control never shows
    /// entries (snap, always-on-top) that make no sense for it.</summary>
    private void TitleGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!ShowWindowMenu)
            e.Handled = true;
    }

    /// <summary>The Alt+Space equivalent MainWindow's PreviewKeyDown calls - the standard Windows
    /// accelerator for a window's system menu, which a frameless window never gets for free.</summary>
    internal void OpenWindowMenu()
    {
        if (!ShowWindowMenu)
            return;
        WindowMenu.PlacementTarget = this;
        WindowMenu.IsOpen = true;
    }

    /// <summary>Set by MainWindow only - keeps the window menu's checkable entry in sync when
    /// "always on top" changes from the tray or Settings instead of from this menu itself.</summary>
    internal void SetAlwaysOnTop(bool value) => AlwaysOnTopMenuItem.IsChecked = value;

    private void RefreshWindowMenuCollapseHeader()
    {
        if (CollapseMenuItem is null)
            return;
        var loc = LocalizationService.Instance;
        CollapseMenuItem.Header = IsCollapsed ? loc["TitleBar.Expand"] : loc["TitleBar.Collapse"];
    }

    /// <summary>IsCheckable="True" already flips IsChecked before Click fires, so this just reports
    /// the new state onward - the same "checked value wins" contract TrayService's CheckOnClick
    /// item uses.</summary>
    private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e) => AlwaysOnTopToggleRequested?.Invoke(this, AlwaysOnTopMenuItem.IsChecked);

    internal static bool ShouldOpenEyePopup(bool isOpen, long msSinceClose) =>
        !isOpen && msSinceClose > EyeReopenGuardMs;

    private void EyePopup_Closed(object? sender, EventArgs e) => _eyeClosedAtTicks = Environment.TickCount64;

    private void EyeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldOpenEyePopup(EyePopup.IsOpen, Environment.TickCount64 - _eyeClosedAtTicks))
            return;
        _eyeOpenedByKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        EyePopup.IsOpen = true;
    }

    // Opened by keyboard, the first row takes focus so the arrow keys and Tab work at once. Opened
    // by mouse, the panel itself takes focus instead: Escape and Tab still work, but no row shows
    // the keyboard focus highlight the mouse user never asked for.
    private void EyePopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_eyeOpenedByKeyboard)
                FindFirstButton(EyePopupContent)?.Focus();
            else
                EyePopupContent.Focus();
        }));

    private void EyePopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        EyePopup.IsOpen = false;
        EyeButton.Focus();
        e.Handled = true;
    }

    private static System.Windows.Controls.Button? FindFirstButton(DependencyObject? root)
    {
        if (root is null)
            return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button button)
                return button;
            if (FindFirstButton(child) is { } found)
                return found;
        }
        return null;
    }
}
