using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class FatalHandlerTests
{
    [Fact]
    public void Show_runs_the_dialog_directly_when_already_on_the_target_thread()
    {
        var calls = 0;

        FatalHandler.Show(
            showDialog: () => calls++,
            checkAccess: () => true,
            invoke: _ => throw new InvalidOperationException("must not marshal when already on the target thread"),
            isShuttingDown: () => false,
            logShuttingDown: () => throw new InvalidOperationException("must not log the shutting-down path outside a shutdown"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Show_marshals_through_invoke_exactly_once_from_a_non_dispatcher_thread()
    {
        var calls = 0;
        var invokeCalls = 0;

        FatalHandler.Show(
            showDialog: () => calls++,
            checkAccess: () => false,
            invoke: action =>
            {
                invokeCalls++;
                action();
            },
            isShuttingDown: () => false,
            logShuttingDown: () => throw new InvalidOperationException("must not log the shutting-down path outside a shutdown"));

        Assert.Equal(1, calls);
        Assert.Equal(1, invokeCalls);
    }

    // The crash this guards against: a second, unrelated exception reached while an earlier crash's
    // own Shutdown() is already tearing the application down. Building a CrashWindow then throws
    // from Application.LoadComponent instead of showing anything, so this path must never even try.
    [Fact]
    public void Show_logs_and_never_builds_the_dialog_when_the_application_is_already_shutting_down()
    {
        var dialogCalls = 0;
        var logCalls = 0;

        FatalHandler.Show(
            showDialog: () => dialogCalls++,
            checkAccess: () => throw new InvalidOperationException("must not check dispatcher access while shutting down"),
            invoke: _ => throw new InvalidOperationException("must not marshal while shutting down"),
            isShuttingDown: () => true,
            logShuttingDown: () => logCalls++);

        Assert.Equal(0, dialogCalls);
        Assert.Equal(1, logCalls);
    }
}
