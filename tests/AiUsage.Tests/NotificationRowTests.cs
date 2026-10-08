using AiUsage.Models;
using AiUsage.ViewModels;

namespace AiUsage.Tests;

/// <summary>The collapsible per-provider notification row and the tile facts it leans on: which
/// window kinds a provider really has, and an account name that survives a nameless tick.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class NotificationRowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    private static ProviderSnapshot Snapshot(
        IReadOnlyList<UsageWindow> windows, ProviderStatus status = ProviderStatus.Ok, string? accountLabel = null) =>
        new("cursor", windows, null, SourceKind.WebSession, Now, Now, status, null, AccountLabel: accountLabel);

    [Fact]
    public void A_tile_offers_every_window_kind_until_its_provider_has_reported_any()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");

        Assert.True(tile.HasFiveHourWindow);
        Assert.True(tile.HasWeeklyWindow);
        Assert.True(tile.HasOtherWindow);
    }

    [Fact]
    public void A_provider_with_only_other_windows_offers_no_five_hour_or_weekly_switch()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");

        tile.Apply(Snapshot([new UsageWindow("Window_CursorModels", WindowKind.Other, 29, Now.AddDays(10), null)]), Now);

        Assert.False(tile.HasFiveHourWindow);
        Assert.False(tile.HasWeeklyWindow);
        Assert.True(tile.HasOtherWindow);
    }

    [Fact]
    public void A_snapshot_without_windows_keeps_the_known_window_kinds()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(Snapshot([new UsageWindow("Window_CursorModels", WindowKind.Other, 29, Now.AddDays(10), null)]), Now);

        tile.Apply(Snapshot([], ProviderStatus.Failed), Now);

        Assert.False(tile.HasFiveHourWindow);
    }

    [Fact]
    public void A_tick_without_an_account_name_keeps_the_known_one_until_a_sign_out()
    {
        var email = "user" + "@" + "example.com";
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(Snapshot([], accountLabel: email), Now);

        tile.Apply(Snapshot([], ProviderStatus.Failed), Now);
        Assert.Equal(email, tile.AccountText);

        tile.Apply(Snapshot([], ProviderStatus.NotSignedIn), Now);
        Assert.Null(tile.AccountText);
    }

    [Fact]
    public void The_collapsed_summary_follows_the_switches_inside()
    {
        var row = new NotificationRowViewModel("codex", "Codex", new ProviderSettings(), () => { });
        Assert.False(row.IsExpanded);
        var standard = row.SummaryText;

        row.Threshold.UseCustom = true;
        var custom = row.SummaryText;

        row.NotificationsEnabled = false;
        var off = row.SummaryText;

        Assert.Equal(3, new[] { standard, custom, off }.Distinct().Count());
    }

    [Fact]
    public void The_row_and_its_threshold_write_into_the_same_provider_settings()
    {
        var settings = new ProviderSettings();
        var row = new NotificationRowViewModel("codex", "Codex", settings, () => { });

        row.NotifyOnResetEnabled = false;
        row.Threshold.UseCustom = true;

        Assert.False(settings.NotifyOnResetEnabled);
        Assert.True(settings.Thresholds.UseCustom);
    }
}
