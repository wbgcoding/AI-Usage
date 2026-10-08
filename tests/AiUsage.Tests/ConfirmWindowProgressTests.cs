using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The work dialog the WebView2 install runs in: the real <see cref="ConfirmWindow"/> in its progress
/// mode, built off screen (no dialog loop, like the other window tests of this suite). Covers the
/// shape it opens in, the phase change at the install's point of no return, and that the cancel
/// button, Esc and the close button ask the work to stop instead of closing the window under it.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class ConfirmWindowProgressTests
{
    private sealed record Shape(
        Visibility Confirm, Visibility Cancel, Visibility Progress, bool Indeterminate, double Value, string Message,
        bool CancelEnabledAtStart, bool CancelEnabledAfterPhase, bool IndeterminateAfterPhase, string MessageAfterPhase,
        bool CancelClickAsked, bool CloseAsked);

    [Fact]
    public void TheWorkDialogOpensWithTheBarAndOnlyCancelAndStopsTheWorkInsteadOfClosing()
    {
        var shape = RunOnSta(() =>
        {
            using var cancellation = new CancellationTokenSource();
            var dialog = ConfirmWindow.CreateWorkDialog("T", "Downloading", "Cancel", cancellation);

            var confirm = dialog.ConfirmButton.Visibility;
            var cancel = dialog.CancelButton.Visibility;
            var progress = dialog.WorkProgress.Visibility;
            var indeterminate = dialog.WorkProgress.IsIndeterminate;
            var enabledAtStart = dialog.CancelButton.IsEnabled;

            dialog.ReportShare(0.5);
            var value = dialog.WorkProgress.Value;

            // The cancel button asks the work to stop; the window itself stays.
            dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var clickAsked = cancellation.IsCancellationRequested;

            dialog.EnterWorkPhase("Installing");

            using var second = new CancellationTokenSource();
            var other = ConfirmWindow.CreateWorkDialog("T", "Downloading", "Cancel", second);
            other.Close();
            var closeAsked = second.IsCancellationRequested;

            return new Shape(
                confirm, cancel, progress, indeterminate, value, dialog.MessageText.Text,
                enabledAtStart, dialog.CancelButton.IsEnabled, dialog.WorkProgress.IsIndeterminate, dialog.MessageText.Text,
                clickAsked, closeAsked);
        });

        Assert.Equal(Visibility.Collapsed, shape.Confirm);
        Assert.Equal(Visibility.Visible, shape.Cancel);
        Assert.Equal(Visibility.Visible, shape.Progress);
        Assert.False(shape.Indeterminate);
        Assert.Equal(0.5, shape.Value);
        Assert.True(shape.CancelEnabledAtStart);
        Assert.False(shape.CancelEnabledAfterPhase, "cancelling is switched off once the install is running");
        Assert.True(shape.IndeterminateAfterPhase);
        Assert.Equal("Installing", shape.MessageAfterPhase);
        Assert.True(shape.CancelClickAsked, "the cancel button asks the work to stop");
        Assert.True(shape.CloseAsked, "closing the window asks the work to stop");
    }

    [Fact]
    public void OncePastThePointOfNoReturnEscAndTheCloseButtonDoNotCancelTheWork()
    {
        var asked = RunOnSta(() =>
        {
            static void PressEscape(ConfirmWindow dialog) => dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "x", IntPtr.Zero),
                0, System.Windows.Input.Key.Escape)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });

            // Control: before the point of no return Esc does ask the work to stop.
            using var early = new CancellationTokenSource();
            PressEscape(ConfirmWindow.CreateWorkDialog("T", "Downloading", "Cancel", early));

            using var cancellation = new CancellationTokenSource();
            var dialog = ConfirmWindow.CreateWorkDialog("T", "Downloading", "Cancel", cancellation);
            dialog.EnterWorkPhase("Installing");

            PressEscape(dialog);
            var afterEsc = cancellation.IsCancellationRequested;

            dialog.Close();
            return (early.IsCancellationRequested, afterEsc, cancellation.IsCancellationRequested);
        });

        Assert.True(asked.Item1, "Esc asks the work to stop while it still can be stopped");
        Assert.False(asked.Item2, "Esc must not cancel work that can no longer be stopped");
        Assert.False(asked.Item3, "closing must not cancel work that can no longer be stopped");
    }

    [Fact]
    public void ADialogWithADetailLineShowsItSelectableAndNamedForScreenReaders()
    {
        var detail = RunOnSta(() =>
        {
            var dialog = new ConfirmWindow("T", "Message", "Open", "Cancel", detailText: "install: exit code 5");
            return (dialog.DetailBox.Visibility, dialog.DetailBox.Text, dialog.DetailBox.IsReadOnly,
                System.Windows.Automation.AutomationProperties.GetName(dialog.DetailBox),
                new ConfirmWindow("T", "Message", "Open", "Cancel").DetailBox.Visibility);
        });

        Assert.Equal(Visibility.Visible, detail.Item1);
        Assert.Equal("install: exit code 5", detail.Item2);
        Assert.True(detail.Item3);
        Assert.Equal("install: exit code 5", detail.Item4);
        Assert.Equal(Visibility.Collapsed, detail.Item5);
    }

    private static T RunOnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
            var app = new AiUsage.App();
            app.InitializeComponent();
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Application.Current?.Shutdown();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
            }
        })
        { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("work dialog test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("work dialog test failed", failure);
        return result!;
    }
}
