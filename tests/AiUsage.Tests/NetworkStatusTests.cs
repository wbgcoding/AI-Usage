using System.Net.NetworkInformation;
using AiUsage.Services;

namespace AiUsage.Tests;

public class NetworkStatusTests
{
    [Fact]
    public async Task A_substituted_probe_is_visible_only_to_the_flow_that_set_it()
    {
        var setterReady = new TaskCompletionSource();
        var observerDone = new TaskCompletionSource();

        var setter = Task.Run(async () =>
        {
            NetworkStatus.Probe = () => false;
            setterReady.SetResult();
            await observerDone.Task;
            return NetworkStatus.HasInternet();
        });

        var observer = Task.Run(async () =>
        {
            await setterReady.Task;
            var seen = NetworkStatus.Probe;
            observerDone.SetResult();
            return seen;
        });

        Assert.False(await setter);
        Assert.Equal(new Func<bool>(NetworkInterface.GetIsNetworkAvailable), await observer);
    }
}
