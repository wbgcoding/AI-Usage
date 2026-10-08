using AiUsage.Models;
using AiUsage.Providers;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Cursor reads through the app's own web session. These tests drive the provider with a canned
/// <see cref="WebUsageResult"/> instead of a live <see cref="WebUsageSource"/>, so each outcome's
/// snapshot is proven without a real browser session.
/// </summary>
public class CursorProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private static readonly UsageWindow Window = new("Window_Month", WindowKind.Other, 42, Now.AddMonths(1), windowMinutes: null);

    private static CursorProvider Provider(Func<CancellationToken, Task<WebUsageResult>> read) => new(read, () => Now);

    private static CursorProvider Provider(WebUsageResult result) => Provider(_ => Task.FromResult(result));

    [Fact]
    public async Task An_ok_result_with_windows_shows_as_ok_through_the_web_session()
    {
        var snapshot = await Provider(new WebUsageResult(WebUsageOutcome.Ok, [Window])).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(SourceKind.WebSession, snapshot.SourceKind);
        Assert.Same(Window, Assert.Single(snapshot.Windows));
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public async Task An_ok_result_without_windows_reads_as_a_failure_not_a_zero()
    {
        var snapshot = await Provider(new WebUsageResult(WebUsageOutcome.Ok, [])).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        // Names what actually happened (a response that parsed but carried no usable window), rather
        // than the same bare failure a dead connection leaves on the tile.
        Assert.Equal("Status_UnreadableAnswer_Reason", snapshot.Error?.ReasonKey);
        Assert.Equal("Action_Retry", snapshot.Error?.ActionKey);
    }

    [Fact]
    public async Task Not_signed_in_shows_the_sign_in_state()
    {
        var snapshot = await Provider(WebUsageResult.NotSignedIn).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_blocked_result_shows_the_blocked_state()
    {
        var snapshot = await Provider(WebUsageResult.Blocked).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Blocked, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_failed_result_reports_a_failure_not_a_zero()
    {
        var snapshot = await Provider(WebUsageResult.Failed).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_returns_a_snapshot_instead_of_throwing_when_the_source_throws()
    {
        var provider = Provider((Func<CancellationToken, Task<WebUsageResult>>)(_ => throw new InvalidOperationException("boom")));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Failed, snapshot.Status);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task A_failed_read_reports_offline_when_the_machine_has_no_connection()
    {
        AiUsage.Services.NetworkStatus.Probe = () => false;
        try
        {
            var snapshot = await Provider(WebUsageResult.Failed).FetchAsync(CancellationToken.None);

            Assert.Equal(ProviderStatus.Failed, snapshot.Status);
            Assert.Equal("Status_Offline_Reason", snapshot.Error?.ReasonKey);
        }
        finally
        {
            AiUsage.Services.NetworkStatus.Probe = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        }
    }

    [Fact]
    public void Cursor_signs_in_through_the_apps_own_web_session()
    {
        var provider = Provider(WebUsageResult.NotSignedIn);

        Assert.True(provider.SupportsInAppSignIn);
    }
}
