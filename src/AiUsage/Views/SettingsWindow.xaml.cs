using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>
/// The Settings window. Everything testable lives in
/// <see cref="SettingsViewModel"/>; this code-behind only owns what genuinely needs a live window or
/// an OS-level side effect (the reset confirmation dialog, the About window, and Claude's
/// sign-in/out - the same split MainWindow.xaml.cs already uses for tray/window glue).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private string? _aboutReleaseUrl;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        WindowChromeNative.Bootstrap(this);
        _viewModel.Main.CopilotLoginChoiceRequested += Main_CopilotLoginChoiceRequested;
        _viewModel.ConfirmRemoveAccount = ConfirmRemoveAccount;

        DataContext = _viewModel;
        Width = _viewModel.SettingsWidth;

        // Esc closes dialogs, keyboard-only throughout.
        PreviewKeyDown += (_, e) => { if (EscapeKey.ClosesWindow(e)) Close(); };

        // Owner is only set after the constructor returns (object initializer), so the work area
        // this window will actually appear on is only known once it is about to show.
        Loaded += SettingsWindow_Loaded;
        Closed += (_, _) => _viewModel.Main.CopilotLoginChoiceRequested -= Main_CopilotLoginChoiceRequested;

        InitializeAboutSection();

        // The About page is filled from code with resolved strings and brushes, so a language or theme
        // switch while this window is open has to rebuild it.
        LocalizationService.Instance.PropertyChanged += OnAboutInputsChanged;
        ThemeService.Applied += OnAboutInputsChanged;
        Closed += (_, _) =>
        {
            LocalizationService.Instance.PropertyChanged -= OnAboutInputsChanged;
            ThemeService.Applied -= OnAboutInputsChanged;
        };
    }

    private void OnAboutInputsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAboutSection);
            return;
        }

        RefreshAboutSection();
    }

    /// <summary>Rebuilds every part of the About page that holds a resolved string or brush, keeping
    /// the provider groups that are open.</summary>
    internal void RefreshAboutSection()
    {
        var openGroups = AboutReadLocationsPanel.Children.OfType<System.Windows.Controls.StackPanel>()
            .Select(group => group.Children.Count > 1 && group.Children[1].Visibility == Visibility.Visible)
            .ToList();

        AboutReadLocationsPanel.Children.Clear();
        FillAboutStaticTexts();
        BuildAboutReadLocationsList();

        var groups = AboutReadLocationsPanel.Children.OfType<System.Windows.Controls.StackPanel>().ToList();
        for (var i = 0; i < groups.Count && i < openGroups.Count; i++)
        {
            if (openGroups[i] && groups[i].Children[0] is System.Windows.Controls.Button header)
                header.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }

        ApplyAboutUpdateStatus();
    }


    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>Turns the next key combination pressed while the capture box is focused into the
    /// stored hotkey text - a key without Ctrl, Alt or Win held (Shift alone is enough for F1-F24) is
    /// refused with Settings.HotkeyNeedsModifier instead of being written, since such a global hotkey
    /// would grab that key everywhere.</summary>
    private void HotkeyCaptureBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Tab and Shift+Tab stay focus navigation, so a keyboard user can always leave the box;
        // only a Tab combined with Ctrl, Alt or Win is taken as a hotkey.
        if (key == Key.Tab && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            return;

        e.Handled = true;
        if (IsModifierOnly(key))
            return;

        if (!GlobalHotkey.HasRequiredModifier(Keyboard.Modifiers, key))
        {
            _viewModel.Main.HotkeyNeedsModifier = true;
            return;
        }

        _viewModel.Main.HotkeyNeedsModifier = false;
        _viewModel.Main.HotkeyText = GlobalHotkey.Format(Keyboard.Modifiers, key);
    }

    private static bool IsModifierOnly(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var anchor = Owner ?? this;
        // By handle first: with mixed scaling the per-monitor areas overlap.
        var current = MainWindow.PickArea(
            NativeMonitors.WorkAreas(),
            NativeMonitors.DeviceNameOfWindow(new System.Windows.Interop.WindowInteropHelper(anchor).Handle),
            new WindowRect(anchor.Left, anchor.Top, anchor.Width, anchor.Height));

        if (current.Height > 0)
            MaxHeight = current.Height;
        if (current.Width > 0)
            MaxWidth = current.Width;

        // CenterOwner next to an edge-docked widget can leave the window partly off-screen.
        UpdateLayout();
        WindowPlacementService.ClampIntoArea(this, current);
    }

    /// <summary>The window has no dedicated height thumb, but <c>WindowChrome</c>'s
    /// <c>ResizeBorderThickness</c> still lets the user drag-resize it natively from the top or
    /// bottom edge - and the moment they do, WPF genuinely overwrites <see cref="Window.SizeToContent"/>
    /// with <see cref="SizeToContent.Manual"/> (readable back off the property, not just an internal
    /// flag). Left alone, every later category switch would then keep the height that drag left
    /// behind instead of fitting the newly selected category.
    /// <para>The guard matters as much as the reset: toggling <c>Manual</c> then back to <c>Height</c>
    /// unconditionally - even when nothing was ever dragged - froze the window at whatever height the
    /// PREVIOUS category left it at, because the toggle captured that still-stale height as "Manual"
    /// before this switch's own layout pass had shrunk anything. Only actually reassert it when a
    /// drag really did leave the property sitting on <c>Manual</c>; otherwise WPF's own
    /// <c>SizeToContent="Height"</c> already recomputes correctly on its own.</para></summary>
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeToContent != SizeToContent.Height)
            SizeToContent = SizeToContent.Height;
    }

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        (Width, Left) = SettingsWindowResize.FromLeftEdge(ActualWidth, Left, e.HorizontalChange, MinWidth, MaxWidth);
    }

    private void ResizeRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = SettingsWindowResize.FromRightEdge(ActualWidth, Left, e.HorizontalChange, MinWidth, MaxWidth).Width;
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e) =>
        _viewModel.SaveSettingsWidth(Width);

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var confirmWindow = new ConfirmWindow(
            loc["Settings.ResetButton"],
            loc["Settings.ResetConfirm"],
            loc["Settings.ResetButton"],
            loc["Action.Cancel"],
            isDestructive: true,
            showExtraOption: true,
            extraOptionText: loc["Settings.ResetAlsoHistory"]);
        OwnerWindowResolver.ApplyOwner(confirmWindow, this);

        if (confirmWindow.ShowDialog() == true)
            _viewModel.ResetToDefaults(confirmWindow.ExtraOptionChecked);
    }

    /// <summary>Asks before an account is removed: its sign-in in this app is deleted, its recorded
    /// history only when the box is ticked. Cancel (or closing the dialog) removes nothing.</summary>
    private (bool Confirmed, bool AlsoHistory) ConfirmRemoveAccount()
    {
        var loc = LocalizationService.Instance;
        var confirmWindow = new ConfirmWindow(
            loc["Settings.RemoveAccount"],
            loc["Settings.RemoveAccount.Confirm"],
            loc["Settings.RemoveAccount"],
            loc["Action.Cancel"],
            isDestructive: true,
            showExtraOption: true,
            extraOptionText: loc["Settings.RemoveAccount.AlsoHistory"]);
        OwnerWindowResolver.ApplyOwner(confirmWindow, this);

        var confirmed = confirmWindow.ShowDialog() == true;
        return (confirmed, confirmed && confirmWindow.ExtraOptionChecked);
    }

    /// <summary>Copilot's "add account" has no web session to sign into, so
    /// <see cref="MainViewModel.AddAccount"/> raises this instead of opening one directly: an empty
    /// candidate list means nothing more to offer (informational only), otherwise each already
    /// signed-in GitHub login is offered in turn as a plain yes/no confirmation - declining moves to
    /// the next, accepting one finishes the add and stops. Reuses <see cref="ConfirmWindow"/> instead
    /// of a new list-picker window, since this is the only place that ever needs to choose among more
    /// than one candidate.</summary>
    private void Main_CopilotLoginChoiceRequested(object? sender, IReadOnlyList<string> candidates)
    {
        var loc = LocalizationService.Instance;
        if (candidates.Count == 0)
        {
            var info = new ConfirmWindow(
                loc["Settings.AddAccount"], loc["Settings.Copilot.NoCandidates"], loc["Action.Ok"], "");
            OwnerWindowResolver.ApplyOwner(info, this);
            info.ShowDialog();
            return;
        }

        foreach (var login in candidates)
        {
            var confirm = new ConfirmWindow(
                loc["Settings.AddAccount"],
                loc.Format("Settings.Copilot.ConfirmLogin", login),
                loc["Settings.AddAccount"],
                loc["Action.Cancel"]);
            OwnerWindowResolver.ApplyOwner(confirm, this);
            if (confirm.ShowDialog() == true)
            {
                _viewModel.Main.FinishAddingCopilotAccount(login);
                return;
            }
        }
    }

    /// <summary>Same branch MainWindow.xaml.cs's own WireSignIn makes from a tile's action button,
    /// with this window as the owner instead - <see cref="WebSignInFlow.Open"/> blocks the whole call
    /// (ShowDialog is modal), so disabling the clicked button around it is only there for the brief
    /// moment before the sign-in window actually opens.</summary>
    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not ProviderTileViewModel tile)
            return;

        var spinner = button.FindName("SignInSpinner") as UIElement;
        button.IsEnabled = false;
        StartSpin(spinner);
        try
        {
            if (!tile.SupportsInAppSignIn)
            {
                // Copilot or the primary Claude account: no web session of its own - see
                // MainWindow.WireSignIn's own branch for an account read through another tool.
                if (tile.RealProviderId == "copilot" && !GitHubCliUsage.IsCliInstalled())
                {
                    ShowGitHubCliMissingNotice();
                    return;
                }
                _viewModel.Main.Reconnect(tile.ProviderId);
                return;
            }

            WebSignInFlow.Open(this, ProviderRegistry.WebSessionFor(tile.ProviderId),
                signedIn => _viewModel.Main.CompleteSignIn(tile.ProviderId, signedIn));
        }
        finally
        {
            StopSpin(spinner);
            button.IsEnabled = true;
        }
    }

    /// <summary>Same themed offer <see cref="WebSignInFlow"/> makes for a missing WebView2 runtime,
    /// for the GitHub CLI Copilot reads through instead.</summary>
    private void ShowGitHubCliMissingNotice()
    {
        var loc = LocalizationService.Instance;
        var openDownloadPage = ConfirmWindow.Show(
            this,
            loc["State.GitHubCliMissing.Head"],
            loc["State.GitHubCliMissing.Reason"],
            loc["State.GitHubCliMissing.Action"],
            loc["Action.Cancel"]);

        if (!openDownloadPage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("https://cli.github.com/") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }

    /// <summary>Same two calls MainWindow.xaml.cs's own SignOutRequested handler makes
    /// (<see cref="MainViewModel.TrySignOutAsync"/>, and on a false result
    /// <see cref="MainViewModel.ShowSignOutIncomplete"/>) - the button stays disabled and the spinner
    /// stays up for the whole await, so a second click cannot start a second sign-out for the same
    /// account while the first is still clearing its browser profile.</summary>
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not ProviderTileViewModel tile)
            return;

        var spinner = button.FindName("SignOutSpinner") as UIElement;
        button.IsEnabled = false;
        StartSpin(spinner);
        try
        {
            var signedOut = await _viewModel.Main.TrySignOutAsync(tile.ProviderId);
            if (!signedOut)
                _viewModel.Main.ShowSignOutIncomplete(tile.ProviderId);
        }
        finally
        {
            StopSpin(spinner);
            button.IsEnabled = true;
        }
    }

    /// <summary>Shows the refresh glyph next to a sign-in or sign-out button and lets it turn; the
    /// glyph itself stays still when Windows has animations turned off.</summary>
    private static void StartSpin(UIElement? spinner)
    {
        if (spinner is not RefreshGlyph glyph)
            return;

        glyph.Visibility = Visibility.Visible;
        glyph.IsSpinning = true;
    }

    private static void StopSpin(UIElement? spinner)
    {
        if (spinner is not RefreshGlyph glyph)
            return;

        glyph.IsSpinning = false;
        glyph.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the About category panel once, the same facts the old standalone About window showed
    /// (see AppInfo) - nothing here duplicates a literal AppInfo already carries.
    /// </summary>
    private void InitializeAboutSection()
    {
        FillAboutStaticTexts();
        BuildAboutReadLocationsList();

        _ = ShowAboutUpdateStatusAsync();
    }

    private void FillAboutStaticTexts()
    {
        var loc = LocalizationService.Instance;
        AboutProductNameText.Text = AppInfo.ProductName;
        AboutVersionText.Text = loc.Format("About.Version", AppInfo.Version);
        AboutCopyrightText.Text = AppInfo.Copyright;
        // Shown only for the unusual cases - an ordinary installed copy with the plain default data
        // folder says nothing extra here at all.
        if (!AppInfo.IsInstalled)
        {
            AboutDeliveryFormText.Text = loc["About.Portable"];
            AboutDeliveryFormText.Visibility = Visibility.Visible;
        }
        AboutArchiveLinkBox.Text = AppInfo.ArchiveUrl;
    }

    private void AboutOpenArchive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ArchiveUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }

    private void AboutCopyAll_Click(object sender, RoutedEventArgs e) =>
        ClipboardHelper.SetTextSafely(SupportReport.Build(_viewModel.Main.Tiles));

    private void AboutOpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutReleaseUrl is null)
            return;

        UpdateDialogs.OpenReleasePage(_aboutReleaseUrl);
    }

    private async void AboutInstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await UpdateDialogs.InstallAsync(this, _viewModel.Main.Update);

    /// <summary>
    /// Fire-and-forget: a slow or failed check must never delay the window opening, and any failure
    /// already comes back as null from <see cref="SettingsViewModel.CheckForUpdateAsync"/> - never an
    /// exception. Stays collapsed for every case except "a newer version exists" and "checked, none
    /// does" - a check that is off, not due yet, or failed to reach the server leaves nothing to say.
    /// </summary>
    private async Task ShowAboutUpdateStatusAsync()
    {
        try
        {
            await _viewModel.CheckForUpdateAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // No IsLoaded guard: a check that is not due completes before the window loads, and the
        // controls exist from InitializeComponent on.
        ApplyAboutUpdateStatus();
    }

    /// <summary>Writes what is already known about updates into the About page - also re-run on a
    /// language switch, with no new check.</summary>
    private void ApplyAboutUpdateStatus()
    {
        var update = _viewModel.Main.Update;
        var loc = LocalizationService.Instance;
        if (update.HasUpdate)
        {
            _aboutReleaseUrl = update.ReleaseUrl;
            AboutUpdateStatusText.Text = loc.Format("About.UpdateAvailable", update.AvailableVersion ?? "");
            AboutInstallUpdateButton.Visibility = Visibility.Visible;
            AboutOpenReleasePageButton.Visibility = Visibility.Visible;
        }
        else if (update.IsKnownUpToDate)
        {
            AboutUpdateStatusText.Text = loc["About.UpToDate"];
            AboutInstallUpdateButton.Visibility = Visibility.Collapsed;
            AboutOpenReleasePageButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            return;
        }

        AboutUpdatePanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// One collapsible group per known provider instead of one flat wall-of-text line, generated from
    /// <see cref="IUsageProvider.ReadLocations"/> rather than typed out here - a provider added later
    /// without filling that property in shows up as an empty group, which is visible and therefore
    /// gets fixed, instead of silently doing nothing.
    /// </summary>
    private void BuildAboutReadLocationsList()
    {
        IReadOnlyList<IUsageProvider> providers =
        [
            ProviderRegistry.CreatePrimaryClaudeAccount(), new CodexProvider(), new CursorProvider(),
            new GeminiProvider(), new CopilotProvider(),
        ];

        foreach (var provider in providers)
            AboutReadLocationsPanel.Children.Add(BuildAboutProviderGroup(provider));
    }

    /// <summary>One provider's collapsible group: a header naming the provider and, in the tile's own
    /// words, where its numbers come from, opening into one row per read location plus the usage page
    /// link. Starts collapsed - four providers' worth of paths at once was exactly the wall of text
    /// this replaces, so nothing shows until asked for.</summary>
    private System.Windows.Controls.StackPanel BuildAboutProviderGroup(IUsageProvider provider)
    {
        var loc = LocalizationService.Instance;

        var chevron = new System.Windows.Shapes.Path
        {
            Width = 10,
            Height = 10,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Data = (System.Windows.Media.Geometry)FindResource("Icon.ArrowDown"),
            Fill = (Brush)FindResource("Text.Secondary"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The same words the tile itself shows for this provider (StatusTextMap.SourceBadge, read
        // here through the live tile rather than resolved a second time) - so nothing new has to be
        // written or translated per provider for this caption.
        var sourceLabel = _viewModel.Main.Tiles.FirstOrDefault(tile => tile.RealProviderId == provider.Id)?.SourceBadgeText ?? "";
        var headerText = new System.Windows.Controls.StackPanel();
        headerText.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = provider.DisplayName,
            FontWeight = FontWeights.SemiBold,
        });
        headerText.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = loc.Format("About.ProviderGroupHeader", sourceLabel),
            FontSize = (double)FindResource("Font.Size.Small"),
            Foreground = (Brush)FindResource("Text.Secondary"),
        });

        var headerContent = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal };
        headerContent.Children.Add(chevron);
        headerContent.Children.Add(headerText);

        var content = new System.Windows.Controls.StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(18, 4, 0, 8),
        };
        foreach (var location in provider.ReadLocations)
            content.Children.Add(BuildAboutReadLocationRow(location));

        var usagePage = ProviderLinks.UsagePage(provider.Id);
        if (usagePage is not null)
            content.Children.Add(BuildAboutUsagePageLink(usagePage));

        // Every live tile of this provider type - the first/only account and any further one - so an
        // extra account added through Settings shows up here too instead of only the primary one.
        foreach (var tile in _viewModel.Main.Tiles.Where(tile => tile.RealProviderId == provider.Id))
            content.Children.Add(BuildAboutDiagnosticRow(tile.SourceDiagnosticText));

        var headerButton = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("AiUsageQuietButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = headerContent,
        };
        System.Windows.Automation.AutomationProperties.SetName(headerButton, $"{provider.DisplayName}: {loc.Format("About.ProviderGroupHeader", sourceLabel)}");
        headerButton.Click += (_, _) =>
        {
            var expanded = content.Visibility == Visibility.Visible;
            content.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            chevron.Data = (System.Windows.Media.Geometry)FindResource(expanded ? "Icon.ArrowDown" : "Icon.ArrowUp");
        };

        var group = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        group.Children.Add(headerButton);
        group.Children.Add(content);
        return group;
    }

    /// <summary>One read-location row: the path in the monospace style, trimmed in the middle (see
    /// <see cref="SettingsViewModel.TrimPathMiddle"/> - same reasoning there: the most distinguishing
    /// part of a long path sits at the end), with the untrimmed path in the tooltip only when it was cut.</summary>
    private System.Windows.Controls.TextBox BuildAboutReadLocationRow(string location)
    {
        // Only a path is cut in the middle; a sentence ("the sign-in of Claude Code on this PC") wraps.
        var shown = location.AsSpan().IndexOfAny('/', '\\') >= 0
            ? SettingsViewModel.TrimPathMiddle(location, maxLength: 42)
            : location;
        return new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Text.Secondary"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Mono"),
            FontSize = (double)FindResource("Font.Size.Small"),
            TextWrapping = TextWrapping.Wrap,
            Cursor = System.Windows.Input.Cursors.IBeam,
            Margin = new Thickness(0, 0, 0, 2),
            ToolTip = shown == location ? null : location,
            Tag = location,
            Text = shown,
        };
    }

    /// <summary>One diagnostic row nested inside a provider's group: which source last answered for
    /// one live tile of this provider (<see cref="ProviderTileViewModel.SourceDiagnosticText"/>), one
    /// row per tile so a further account added through Settings shows up here too, not only the
    /// first/only one.</summary>
    private System.Windows.Controls.TextBox BuildAboutDiagnosticRow(string text) => new()
    {
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = (Brush)FindResource("Text.Secondary"),
        FontSize = (double)FindResource("Font.Size.Small"),
        TextWrapping = TextWrapping.Wrap,
        Cursor = System.Windows.Input.Cursors.IBeam,
        Margin = new Thickness(0, 0, 0, 2),
        Text = text,
    };

    /// <summary>The usage-page row: a plain link, styled like every other quiet action in this window,
    /// opened the same way <see cref="AboutOpenReleasePage_Click"/> already opens a URL.</summary>
    private System.Windows.Controls.Button BuildAboutUsagePageLink(Uri usagePage)
    {
        var link = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("AiUsageQuietButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)FindResource("Accent"),
            Content = usagePage.OriginalString,
        };
        link.Click += (_, _) => AboutOpenUrl(usagePage);
        return link;
    }

    private static void AboutOpenUrl(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.OriginalString) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }
}
