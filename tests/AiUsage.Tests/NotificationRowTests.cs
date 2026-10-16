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
    private static NotificationRowViewModel RowFor(ProviderTileViewModel tile) =>
        new(tile.ProviderId, tile.HeaderDisplayName, new ProviderSettings(), () => { }, tile);

    [Fact]
    public void The_spoken_labels_join_their_parts_with_the_resource_pattern()
    {
        var loc = AiUsage.Services.LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude");
        var threshold = RowFor(tile).Threshold;

        Assert.Equal(loc.Format("Format.LabelValue", loc["Window.FiveHour"], threshold.FiveHourLabel), threshold.FiveHourFullLabel);
        Assert.Equal(loc.Format("Format.LabelValue", tile.HeaderDisplayName + " " + loc["Window.Weekly"], loc["Settings.On"]), threshold.WeeklyEnabledFullLabel);
        Assert.Equal(loc.Format("Format.LabelValue", tile.HeaderDisplayName, loc["Settings.OwnThreshold"]), threshold.OwnThresholdFullLabel);
        Assert.Equal(loc.Format("Format.LabelValue", tile.HeaderDisplayName, tile.LastSuccessAgeText), tile.LastSuccessLine);
        Assert.Equal(loc.Format("Format.LabelValue", "Claude", "1.2M"), new AiUsage.Stats.StatsProviderRowViewModel("Claude", "1.2M", "1,200,000").LabelValueText);
        Assert.Contains(": ", loc.Format("Format.LabelValue", "a", "b"));
        Assert.Contains(" / ", loc.Format("Format.Pair", "a", "b"));
    }

    [Fact]
    public void The_own_threshold_row_names_the_real_other_window()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var row = RowFor(tile);
        var otherWord = AiUsage.Services.LocalizationService.Instance["Window.Other"];
        Assert.Equal(otherWord, row.Threshold.OtherWindowLabel);

        tile.Apply(Snapshot([new UsageWindow("Fable", WindowKind.Other, 12, Now.AddDays(3), null)]), Now);

        Assert.Equal("Fable", row.Threshold.OtherWindowLabel);
        Assert.Contains("Fable", row.Threshold.OtherFullLabel, StringComparison.Ordinal);
        Assert.Contains("Fable", row.Threshold.OtherEnabledFullLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_other_windows_are_listed_and_none_falls_back_to_the_generic_word()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var row = RowFor(tile);
        var otherWord = AiUsage.Services.LocalizationService.Instance["Window.Other"];

        tile.Apply(Snapshot(
        [
            new UsageWindow("Fable", WindowKind.Other, 12, Now.AddDays(3), null),
            new UsageWindow("Opus", WindowKind.Other, 30, Now.AddDays(3), null),
            new UsageWindow("Fable", WindowKind.Other, 12, Now.AddDays(3), null),
        ]), Now);
        Assert.Equal("Fable, Opus", row.Threshold.OtherWindowLabel);

        tile.Apply(Snapshot([new UsageWindow("Window_Weekly", WindowKind.Weekly, 40, Now.AddDays(3), 10080)]), Now);
        Assert.Equal(otherWord, row.Threshold.OtherWindowLabel);
    }
}
