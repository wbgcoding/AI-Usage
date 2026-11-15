using System.Windows.Threading;
using AiUsage.ViewModels;

namespace AiUsage.Tests;

public class UiThreadTests
{
    [Fact]
    public void Without_a_dispatcher_the_action_runs_inline()
    {
        var ran = 0;

        UiThread.Run(null, () => ran++);

        Assert.Equal(1, ran);
    }

    [Fact]
    public void On_the_dispatcher_thread_the_action_runs_inline()
    {
        var ranInline = false;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var ran = false;
            UiThread.Run(dispatcher, () => ran = true);
            ranInline = ran;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(ranInline);
    }

    [Fact]
    public void From_another_thread_the_action_is_queued_on_the_dispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        owner.SetApartmentState(ApartmentState.STA);
        owner.IsBackground = true;
        owner.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        var ranOn = -1;
        UiThread.Run(dispatcher, () =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(owner.ManagedThreadId, ranOn);
        dispatcher!.InvokeShutdown();
        owner.Join();
    }
}
