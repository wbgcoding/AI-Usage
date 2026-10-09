using System.Windows;
using System.Windows.Input;
using AiUsage.Services;

namespace AiUsage.Views;

/// <summary>
/// The app's own themed replacement for the framework's yes/no confirmation dialog: same chrome
/// as every other window, returns a bool instead of a framework-specific result enum.
/// </summary>
public partial class ConfirmWindow : Window
{
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }

    /// <summary>Whether the optional extra checkbox (e.g. "also delete the recorded history")
    /// shows at all - every caller except the Settings reset confirmation leaves this false, keeping
    /// today's plain two-button shape.</summary>
    public bool ShowExtraOption { get; }

    public string ExtraOptionText { get; }

    /// <summary>Read back by the caller once <see cref="ShowDialog"/> returns true. Defaults
    /// unchecked - pressing Enter must keep the safe answer (history survives) rather than silently
    /// opting into the destructive one.</summary>
    public bool ExtraOptionChecked { get; set; }

    // Set only while RunWithProgress owns this window: the work the cancel button, Esc and the close
    // button stop instead of closing the window out from under it.
    private CancellationTokenSource? _workCancellation;
    private bool _workRunning;
    // Set at the work's point of no return: from then on nothing can stop it, so Esc, the close
    // button and Closing are ignored instead of making the dialog report a cancel the work ignores.
    private bool _pastPointOfNoReturn;

    /// <param name="detailText">A short technical line shown under the message, selectable so it can
    /// be copied into a report; empty shows nothing.</param>
    public ConfirmWindow(string title, string message, string confirmText, string cancelText, bool isDestructive = false,
        bool showExtraOption = false, string extraOptionText = "", string detailText = "")
    {
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        ShowExtraOption = showExtraOption;
        ExtraOptionText = extraOptionText;

        InitializeComponent();
        WindowChromeNative.Bootstrap(this);
        DataContext = this;
        Title = title;
        TitleBarControl.TitleText = title;

        if (isDestructive)
            ConfirmButton.Style = (Style)FindResource("AiUsageDangerButton");

        // No cancel text: a plain notice with a single OK button, which Enter and Esc both close.
        if (string.IsNullOrEmpty(cancelText))
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Margin = new Thickness(0);
            ConfirmButton.IsDefault = true;
        }

        if (detailText.Length > 0)
        {
            DetailBox.Text = detailText;
            DetailBox.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(DetailBox, detailText);
            // One click puts the whole line in the selection, ready to copy.
            DetailBox.GotKeyboardFocus += (_, _) => DetailBox.SelectAll();
            DetailBox.PreviewMouseLeftButtonUp += (_, _) => DetailBox.SelectAll();
        }

        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        FitWidthToButtons();

        // Esc closes dialogs, keyboard-only throughout.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) RequestClose(); };
        Closing += (_, e) =>
        {
            // A running work is stopped, never abandoned: the window closes once the work reports back.
            if (_workRunning)
            {
                e.Cancel = true;
                if (!_pastPointOfNoReturn)
                    _workCancellation?.Cancel();
            }
        };
    }

    /// <summary>What the cancel button, Esc and the title bar's close button do: close the dialog, or
    /// - while <see cref="RunWithProgress{T}"/> is running its work - ask that work to stop.</summary>
    private void RequestClose()
    {
        if (_workRunning)
        {
            if (!_pastPointOfNoReturn)
                _workCancellation?.Cancel();
            return;
        }

        DialogResult = false;
    }

    /// <summary>Widens the window before it shows when the two buttons do not fit side by side at
    /// the default width (long labels in a wide font). Done up front on purpose: letting the window
    /// size its width to the content, or widening it once loaded, leaves it unpainted or clipped.</summary>
    private void FitWidthToButtons()
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var buttons = 0.0;
        foreach (var button in ButtonRow.Children.OfType<FrameworkElement>().Where(b => b.Visibility != Visibility.Collapsed))
        {
            button.Measure(infinite);
            buttons += button.DesiredSize.Width;
        }

        var margin = ((FrameworkElement)ButtonRow.Parent).Margin;
        var border = ChromeBorder.BorderThickness;
        Width = Math.Max(MinWidth, Math.Ceiling(buttons + margin.Left + margin.Right + border.Left + border.Right));
    }

    /// <summary>True only when the user picked the confirm button; false for cancel, Esc or the
    /// close button.</summary>
    public static bool Show(Window owner, string title, string message, string confirmText, string cancelText, bool isDestructive = false,
        string detailText = "")
    {
        var dialog = new ConfirmWindow(title, message, confirmText, cancelText, isDestructive, detailText: detailText);
        OwnerWindowResolver.ApplyOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>The themed replacement for a plain framework message box: one message, one OK
    /// button, owned by <paramref name="owner"/> or the active window.</summary>
    public static void ShowInfo(Window? owner, string message)
    {
        var loc = LocalizationService.Instance;
        var dialog = new ConfirmWindow(AppInfo.ProductName, message, loc["Action.Ok"], "");
        OwnerWindowResolver.ApplyOwner(dialog, owner);
        dialog.ShowDialog();
    }

    /// <summary>Runs <paramref name="work"/> inside a dialog with a progress bar and one cancel button.
    /// The work reports its share done (0 to 1) and, when it reaches a point of no return, calls
    /// <c>enterPhase</c> with the new message: the bar then moves without a share and cancelling is
    /// switched off. The dialog closes by itself once the work returns; closing it earlier asks the
    /// work to stop through the token. Returns what the work returned.</summary>
    public static T RunWithProgress<T>(
        Window? owner, string title, string message, string cancelText,
        Func<IProgress<double>, Action<string>, CancellationToken, Task<T>> work)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = CreateWorkDialog(title, message, cancelText, cancellation);
        OwnerWindowResolver.ApplyOwner(dialog, owner);

        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                result = await work(new Progress<double>(dialog.ReportShare), dialog.EnterWorkPhase, cancellation.Token);
            }
            catch (Exception ex)
            {
                // An async void handler's exception would go straight to the crash path; the caller
                // gets it from this method instead, once the dialog has closed.
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                dialog.FinishWork();
            }
        };
        dialog.ShowDialog();
        failure?.Throw();
        return result;
    }

    /// <summary>The window of <see cref="RunWithProgress{T}"/> before it is shown: cancel button only,
    /// a progress bar, and the cancel button, Esc and the close button wired to
    /// <paramref name="cancellation"/> instead of closing.</summary>
    internal static ConfirmWindow CreateWorkDialog(string title, string message, string cancelText, CancellationTokenSource cancellation)
    {
        var dialog = new ConfirmWindow(title, message, "", cancelText);
        dialog.ConfirmButton.Visibility = Visibility.Collapsed;
        dialog.CancelButton.Margin = new Thickness(0);
        dialog.WorkProgress.Visibility = Visibility.Visible;
        dialog._workCancellation = cancellation;
        dialog._workRunning = true;
        return dialog;
    }

    internal void ReportShare(double share) => WorkProgress.Value = share;

    /// <summary>The work reached its point of no return: new message, a bar without a share, no cancel.</summary>
    internal void EnterWorkPhase(string text)
    {
        _pastPointOfNoReturn = true;
        MessageText.Text = text;
        WorkProgress.IsIndeterminate = true;
        CancelButton.IsEnabled = false;
    }

    private void FinishWork()
    {
        _workRunning = false;
        DialogResult = true;
    }

    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => RequestClose();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RequestClose();
}
