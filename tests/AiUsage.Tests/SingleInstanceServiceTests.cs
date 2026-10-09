using AiUsage.Services;

namespace AiUsage.Tests;

public class SingleInstanceServiceTests
{
    [Fact]
    public void WantsNewInstance_is_true_only_when_the_flag_is_present()
    {
        Assert.True(SingleInstanceService.WantsNewInstance(["--new-instance"], allowed: true));
        Assert.True(SingleInstanceService.WantsNewInstance(["--tray", "--new-instance"], allowed: true));
    }

    [Fact]
    public void WantsNewInstance_ignores_the_flag_when_the_build_does_not_allow_it()
    {
        Assert.False(SingleInstanceService.WantsNewInstance(["--new-instance"], allowed: false));
    }

    [Fact]
    public void A_release_build_never_allows_a_new_instance()
    {
#if DEBUG
        Assert.True(SingleInstanceService.NewInstanceAllowed);
#else
        Assert.False(SingleInstanceService.NewInstanceAllowed);
#endif
    }

    [Fact]
    public void WantsNewInstance_ignores_case()
    {
        Assert.True(SingleInstanceService.WantsNewInstance(["--NEW-INSTANCE"], allowed: true));
    }

    [Fact]
    public void WantsNewInstance_is_false_without_the_flag()
    {
        Assert.False(SingleInstanceService.WantsNewInstance([]));
        Assert.False(SingleInstanceService.WantsNewInstance(["--tray"]));
    }

    [Fact]
    public void AcquireOwnership_returns_true_immediately_when_a_new_instance_is_requested_without_touching_the_mutex()
    {
        using var service = new SingleInstanceService(UniqueMutexName());

        Assert.True(service.AcquireOwnership(["--new-instance"], allowNewInstance: true));
    }

    [Fact]
    public void AcquireOwnership_still_loses_to_a_running_copy_when_the_build_ignores_the_flag()
    {
        var name = UniqueMutexName();
        using var first = new SingleInstanceService(name);
        using var second = new SingleInstanceService(name);

        Assert.True(first.AcquireOwnership([], allowNewInstance: false));
        Assert.False(second.AcquireOwnership(["--new-instance"], allowNewInstance: false));
    }

    [Fact]
    public void A_second_service_cannot_acquire_ownership_while_the_first_still_holds_it()
    {
        // A per-test random name so this never collides with a real running instance of the app,
        // or with another test running in parallel.
        var name = UniqueMutexName();
        using var first = new SingleInstanceService(name);
        using var second = new SingleInstanceService(name);

        Assert.True(first.AcquireOwnership([]));
        Assert.False(second.AcquireOwnership([]));
    }

    [Fact]
    public void A_restarted_copy_takes_over_once_the_previous_one_lets_go_within_the_retry_window()
    {
        var name = UniqueMutexName();
        var first = new SingleInstanceService(name);
        using var second = new SingleInstanceService(name);
        Assert.True(first.AcquireOwnership([]));
        var release = Task.Run(async () =>
        {
            await Task.Delay(400);
            first.Dispose();
        });

        var acquired = second.AcquireOwnership([], allowNewInstance: false, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

        release.Wait();
        Assert.True(acquired);
    }

    [Fact]
    public void A_restarted_copy_gives_up_when_the_previous_one_never_lets_go()
    {
        var name = UniqueMutexName();
        using var first = new SingleInstanceService(name);
        using var second = new SingleInstanceService(name);
        Assert.True(first.AcquireOwnership([]));

        Assert.False(second.AcquireOwnership([], allowNewInstance: false, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task A_show_request_that_arrives_before_the_handler_is_registered_is_not_lost()
    {
        var eventName = $"Local\\AI-Usage-Test-Show-{Guid.NewGuid():N}";
        using var service = new SingleInstanceService(UniqueMutexName(), eventName);
        Assert.True(service.AcquireOwnership([]));

        // Simulates a second start's own RequestShow landing in the gap between this instance
        // acquiring ownership and its main window (and this handler) actually existing.
        SingleInstanceService.RequestShow(eventName);

        var handlerRan = new TaskCompletionSource();
        service.RegisterShowRequestHandler(() => handlerRan.TrySetResult());

        var completed = await Task.WhenAny(handlerRan.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(handlerRan.Task, completed);
    }

    [Fact]
    public void The_show_event_name_and_the_mutex_name_are_different()
    {
        Assert.NotEqual(SingleInstanceService.DefaultMutexName, SingleInstanceService.ShowEventName);
    }

    private static string UniqueMutexName() => $"AI-Usage-Test-{Guid.NewGuid():N}";
}
