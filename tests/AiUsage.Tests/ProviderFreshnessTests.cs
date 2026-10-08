using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class ProviderFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshDataWithAFutureResetIsNotStale()
    {
        var stale = ProviderFreshness.IsStale(Now.AddMinutes(-5), [Now.AddHours(2)], Now);

        Assert.False(stale);
    }

    [Fact]
    public void DataOlderThanTheThresholdIsStale()
    {
        var stale = ProviderFreshness.IsStale(Now - ProviderFreshness.StaleAfter.Add(TimeSpan.FromMinutes(1)), [Now.AddHours(2)], Now);

        Assert.True(stale);
    }

    [Fact]
    public void ExactlyAtTheThresholdIsStillFresh()
    {
        var stale = ProviderFreshness.IsStale(Now - ProviderFreshness.StaleAfter, [Now.AddHours(2)], Now);

        Assert.False(stale);
    }

    [Fact]
    public void APassedResetMakesRecentDataStale()
    {
        var stale = ProviderFreshness.IsStale(Now.AddMinutes(-1), [Now.AddHours(-3), Now.AddMinutes(-1)], Now);

        Assert.True(stale);
    }

    [Fact]
    public void AnExpiredWindowMakesTheSnapshotStaleEvenWhileAnotherWindowIsStillRunning()
    {
        // A five-hour window that reset three hours ago and a weekly window that resets four hours
        // from now: the newest-only check used to fold these into one "newestReset" and only look at
        // the future one, reporting fresh. Every window's reset has to be checked, not just the
        // newest, so the expired one still makes the snapshot stale.
        var stale = ProviderFreshness.IsStale(Now.AddMinutes(-1), [Now.AddHours(-3), Now.AddHours(4)], Now);

        Assert.True(stale);
    }

    [Fact]
    public void WindowsWithoutAResetFallBackToTheAgeAlone()
    {
        Assert.False(ProviderFreshness.IsStale(Now.AddMinutes(-1), [null, null], Now));
        Assert.True(ProviderFreshness.IsStale(Now.AddDays(-2), [null, null], Now));
    }
}
