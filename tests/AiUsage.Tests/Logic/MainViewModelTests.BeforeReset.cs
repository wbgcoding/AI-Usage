using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public partial class MainViewModelTests
{
    // The fake provider's window resets two hours after Now.
    private static DateTimeOffset EightMinutesBeforeReset => Now.AddMinutes(112);

    private (MainViewModel Vm, List<BeforeResetNotification> Raised) BuildBeforeReset(
        double codexPercent, bool notifyBeforeReset = true, bool tileNotifications = true, bool quiet = false)
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", false), ("gemini", false), ("copilot", false));
        settings.NotifyBeforeReset = notifyBeforeReset;
        settings.Providers["codex"].NotificationsEnabled = tileNotifications;
        if (quiet)
        {
            settings.QuietHoursEnabled = true;
            settings.QuietHoursStart = "00:00";
            settings.QuietHoursEnd = "23:59";
        }

        // The hidden accounts sit at the same level on purpose: only a shown tile may be announced.
        var (vm, _, _) = Build(settings, new Dictionary<string, double>
        {
            ["codex"] = codexPercent, ["claude"] = codexPercent, ["gemini"] = codexPercent, ["copilot"] = codexPercent,
        });
        var raised = new List<BeforeResetNotification>();
        vm.BeforeResetRaised += raised.Add;
        vm.Tick(Now);
        return (vm, raised);
    }

    [Fact]
    public void A_full_window_eight_minutes_before_its_reset_is_announced_once()
    {
        var (vm, raised) = BuildBeforeReset(92);

        vm.EvaluateBeforeReset(EightMinutesBeforeReset);
        vm.EvaluateBeforeReset(EightMinutesBeforeReset.AddSeconds(1));
        vm.EvaluateBeforeReset(EightMinutesBeforeReset.AddMinutes(3));

        var notice = Assert.Single(raised);
        Assert.Equal("codex", notice.ProviderId);
        Assert.Equal(8, notice.Minutes);
        Assert.Contains("8", notice.Text());
    }

    [Fact]
    public void A_reset_time_that_wobbles_by_seconds_between_reads_is_still_announced_once()
    {
        var (vm, raised) = BuildBeforeReset(92);
        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        // The next read reports the same reset three seconds later (Codex derives it from the read time).
        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");
        var wobbled = new ProviderSnapshot("codex",
            [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 92, Now.AddHours(2).AddSeconds(3), 300)],
            "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);
        tile.Apply(wobbled, Now);
        vm.EvaluateBeforeReset(EightMinutesBeforeReset.AddMinutes(1));

        Assert.Single(raised);
    }

    [Fact]
    public void The_next_reset_of_the_same_window_is_announced_again()
    {
        var (vm, raised) = BuildBeforeReset(92);
        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        // The provider reports the following reset a week later; the old one is long gone.
        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");
        var next = new ProviderSnapshot("codex",
            [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 95, Now.AddHours(7), 300)],
            "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);
        tile.Apply(next, Now);
        vm.EvaluateBeforeReset(Now.AddHours(7).AddMinutes(-5));

        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void A_window_below_ninety_percent_is_not_announced()
    {
        var (vm, raised) = BuildBeforeReset(80);

        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        Assert.Empty(raised);
    }

    [Fact]
    public void A_reset_more_than_ten_minutes_away_is_not_announced_yet()
    {
        var (vm, raised) = BuildBeforeReset(92);

        vm.EvaluateBeforeReset(Now.AddMinutes(108));
        Assert.Empty(raised);

        vm.EvaluateBeforeReset(Now.AddMinutes(110));
        Assert.Single(raised);
    }

    [Fact]
    public void With_the_switch_off_nothing_is_announced()
    {
        var (vm, raised) = BuildBeforeReset(92, notifyBeforeReset: false);

        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        Assert.Empty(raised);
    }

    [Fact]
    public void A_tile_with_its_notifications_off_is_not_announced()
    {
        var (vm, raised) = BuildBeforeReset(92, tileNotifications: false);

        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        Assert.Empty(raised);
    }

    [Fact]
    public void Quiet_hours_drop_the_announcement()
    {
        var (vm, raised) = BuildBeforeReset(92, quiet: true);

        vm.EvaluateBeforeReset(EightMinutesBeforeReset);

        Assert.Empty(raised);
    }

    [Fact]
    public void The_last_minute_still_says_one_minute()
    {
        var (vm, raised) = BuildBeforeReset(92);

        vm.EvaluateBeforeReset(Now.AddMinutes(120).AddSeconds(-20));

        Assert.Equal(1, Assert.Single(raised).Minutes);
    }
}
