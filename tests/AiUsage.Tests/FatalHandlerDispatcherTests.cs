using System.Windows.Threading;
using AiUsage.Services;

namespace AiUsage.Tests;

public class FatalHandlerDispatcherTests
{
    [Fact]
    public void InvokeWhenResponsive_gives_up_without_running_the_action_when_the_dispatcher_never_starts_it()
    {
        Dispatcher? blocked = null;
        var ran = false;
        // A dispatcher whose thread never pumps messages: the action can never start.
        var thread = new Thread(() =>
        {
            blocked = Dispatcher.CurrentDispatcher;
            Thread.Sleep(1500);
        });
        thread.Start();
        SpinWait.SpinUntil(() => blocked is not null, TimeSpan.FromSeconds(5));

        var result = FatalHandler.InvokeWhenResponsive(blocked!, () => ran = true, TimeSpan.FromMilliseconds(100));

        Assert.False(result);
        Assert.False(ran);
        thread.Join();
    }

    [Fact]
    public void InvokeWhenResponsive_waits_for_an_action_that_outlasts_the_start_timeout()
    {
        Dispatcher? running = null;
        var thread = new Thread(() =>
        {
            running = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        });
        thread.Start();
        SpinWait.SpinUntil(() => running is not null, TimeSpan.FromSeconds(5));
        var finished = false;

        var result = FatalHandler.InvokeWhenResponsive(running!, () =>
        {
            Thread.Sleep(300);
            finished = true;
        }, TimeSpan.FromMilliseconds(100));

        Assert.True(result);
        Assert.True(finished);
        running!.InvokeShutdown();
        thread.Join();
    }
}
