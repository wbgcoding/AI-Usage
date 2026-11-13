using System.Text.Json;
using AiUsage.Models;
using AiUsage.ViewModels;

namespace AiUsage.Tests;

/// <summary>The week-long "plan changed" line on a tile: what counts as a change, how long it shows and
/// that dismissing it sticks.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class PlanChangeNoticeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

    private static ProviderTileViewModel OkTile()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 20, Now.AddHours(3), null);
        tile.Apply(new ProviderSnapshot("claude", [window], "max", SourceKind.WebSession, Now, Now, ProviderStatus.Ok, null), Now);
        return tile;
    }

    [Fact]
    public void The_first_plan_is_only_stored()
    {
        var settings = new ProviderSettings();

        var changed = MainViewModel.ObservePlan(settings, "max", Now);

        Assert.True(changed);
        Assert.Equal("Max", settings.LastPlan);
        Assert.Null(settings.PlanChange);
    }

    [Fact]
    public void A_different_plan_stores_a_change_and_the_same_plan_does_not()
    {
        var settings = new ProviderSettings { LastPlan = "Max" };

        Assert.False(MainViewModel.ObservePlan(settings, "max", Now));
        Assert.Null(settings.PlanChange);

        Assert.True(MainViewModel.ObservePlan(settings, "max20x", Now));

        Assert.Equal("Ultra", settings.LastPlan);
        Assert.NotNull(settings.PlanChange);
        Assert.Equal("Max", settings.PlanChange!.From);
        Assert.Equal("Ultra", settings.PlanChange.To);
        Assert.Equal(Now.ToUniversalTime(), settings.PlanChange.AtUtc);
        Assert.False(settings.PlanChange.Dismissed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-known-tier-word")]
    public void A_read_without_a_plan_is_never_a_change(string? plan)
    {
        var settings = new ProviderSettings { LastPlan = "Max" };

        Assert.False(MainViewModel.ObservePlan(settings, plan, Now));

        Assert.Equal("Max", settings.LastPlan);
        Assert.Null(settings.PlanChange);
    }

    [Fact]
    public void A_change_shows_for_a_week_and_is_hidden_after_eight_days()
    {
        var settings = new ProviderSettings { LastPlan = "Max" };
        MainViewModel.ObservePlan(settings, "ultra", Now);
        var tile = OkTile();

        tile.SetPlanChange(settings.PlanChange, Now.AddDays(1));
        Assert.True(tile.ShowPlanChangeNotice);
        Assert.Contains("Max", tile.PlanChangeText);
        Assert.Contains("Ultra", tile.PlanChangeText);

        tile.SetPlanChange(settings.PlanChange, Now.AddDays(8));
        Assert.False(tile.ShowPlanChangeNotice);
        Assert.Equal("", tile.PlanChangeText);
    }

    [Fact]
    public void Dismissing_hides_the_line_and_persists()
    {
        var settings = new ProviderSettings { LastPlan = "Max" };
        MainViewModel.ObservePlan(settings, "ultra", DateTimeOffset.UtcNow);
        var tile = OkTile();
        var dismissed = 0;
        tile.PlanChangeDismissed += (_, _) => dismissed++;
        tile.SetPlanChange(settings.PlanChange, DateTimeOffset.UtcNow);
        Assert.True(tile.ShowPlanChangeNotice);

        tile.DismissPlanChangeCommand.Execute(null);

        Assert.False(tile.ShowPlanChangeNotice);
        Assert.Equal(1, dismissed);
        Assert.True(settings.PlanChange!.Dismissed);
        var reloaded = JsonSerializer.Deserialize<ProviderSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(reloaded.PlanChange!.Dismissed);
        Assert.Equal("Ultra", reloaded.LastPlan);
        var again = OkTile();
        again.SetPlanChange(reloaded.PlanChange, DateTimeOffset.UtcNow);
        Assert.False(again.ShowPlanChangeNotice);
    }

    [Fact]
    public void The_line_needs_the_bars_to_show()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.SetPlanChange(new StoredPlanChange { From = "Max", To = "Ultra", AtUtc = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);

        Assert.False(tile.ShowPlanChangeNotice);
    }
}
