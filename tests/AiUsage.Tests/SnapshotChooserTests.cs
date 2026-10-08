using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class SnapshotChooserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

    private static UsageWindow Window() => new("Window_FiveHour", WindowKind.FiveHour, 10, null, null);

    private static ProviderSnapshot Snapshot(
        ProviderStatus status = ProviderStatus.Ok,
        SourceKind sourceKind = SourceKind.LocalFile,
        int windowCount = 1,
        DateTimeOffset? dataTimestamp = null) => new(
        ProviderId: "claude",
        Windows: Enumerable.Range(0, windowCount).Select(_ => Window()).ToList(),
        PlanType: null,
        SourceKind: sourceKind,
        FetchedAt: Now,
        DataTimestamp: dataTimestamp ?? Now,
        Status: status,
        Error: null);

    [Fact]
    public void Pick_returns_the_sole_candidate_when_only_one_is_given()
    {
        var only = Snapshot(status: ProviderStatus.NotSignedIn, windowCount: 0);

        var picked = SnapshotChooser.Pick([only]);

        Assert.Same(only, picked);
    }

    [Fact]
    public void Pick_prefers_a_usable_candidate_over_an_unusable_one()
    {
        var usable = Snapshot(status: ProviderStatus.Ok);
        var unusable = Snapshot(status: ProviderStatus.Failed, windowCount: 0);

        var picked = SnapshotChooser.Pick([unusable, usable]);

        Assert.Same(usable, picked);
    }

    [Fact]
    public void Pick_prefers_the_candidate_with_more_windows()
    {
        var twoWindows = Snapshot(windowCount: 2);
        var oneWindow = Snapshot(windowCount: 1);

        var picked = SnapshotChooser.Pick([oneWindow, twoWindows]);

        Assert.Same(twoWindows, picked);
    }

    [Fact]
    public void Pick_prefers_the_newer_timestamp_when_window_counts_are_equal()
    {
        var older = Snapshot(dataTimestamp: Now.AddMinutes(-10));
        var newer = Snapshot(dataTimestamp: Now);

        var picked = SnapshotChooser.Pick([older, newer]);

        Assert.Same(newer, picked);
    }

    [Fact]
    public void Pick_breaks_a_full_tie_with_the_cheaper_source()
    {
        var localFile = Snapshot(sourceKind: SourceKind.LocalFile, dataTimestamp: Now);
        var webSession = Snapshot(sourceKind: SourceKind.WebSession, dataTimestamp: Now);

        var picked = SnapshotChooser.Pick([webSession, localFile]);

        Assert.Same(localFile, picked);
    }

    [Fact]
    public void Pick_prefers_not_signed_in_over_failed_when_nothing_is_usable()
    {
        var failed = Snapshot(status: ProviderStatus.Failed, windowCount: 0, sourceKind: SourceKind.None);
        var notSignedIn = Snapshot(status: ProviderStatus.NotSignedIn, windowCount: 0, sourceKind: SourceKind.None);

        var picked = SnapshotChooser.Pick([failed, notSignedIn]);

        Assert.Same(notSignedIn, picked);
    }
}
