using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class TitleBarTests
{
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 800, false)]
    [InlineData(false, 120, false)]
    [InlineData(false, 800, true)]
    public void EyePopupOnlyOpensWhenClosedAndNotJustClosed(bool isOpen, long msSinceClose, bool expectedOpen) =>
        Assert.Equal(expectedOpen, TitleBar.ShouldOpenEyePopup(isOpen, msSinceClose));

    [Fact]
    public void AClickRightAfterTheCloseStaysClosedAndALaterOneOpens()
    {
        Exception? failure = null;
        (bool Quick, bool Later)? opened = null;

        var worker = new Thread(() =>
        {
            try
            {
                opened = ClickRightAfterAndLongAfterAClose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("title bar test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("title bar test failed.", failure);

        // The click that follows the press which closed the popup must not reopen it; a click well
        // after the close (mouse or keyboard, both end in the same Click) opens it again.
        Assert.False(opened!.Value.Quick);
        Assert.True(opened.Value.Later);
    }

    private static (bool Quick, bool Later) ClickRightAfterAndLongAfterAClose()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            var titleBar = new TitleBar();
            var closedAt = typeof(TitleBar).GetField("_eyeClosedAtTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;

            closedAt.SetValue(titleBar, Environment.TickCount64);
            titleBar.EyeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var quick = Equals(titleBar.EyePopup.ReadLocalValue(Popup.IsOpenProperty), true);

            closedAt.SetValue(titleBar, Environment.TickCount64 - 800);
            titleBar.EyeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (quick, Equals(titleBar.EyePopup.ReadLocalValue(Popup.IsOpenProperty), true));
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }
}
