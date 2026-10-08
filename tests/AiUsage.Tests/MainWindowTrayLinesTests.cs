using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// MainWindow.BuildTrayLines is the pure projection UpdateTray hands to the tray tooltip - pulled
/// out specifically so it is testable without a live window (same pattern as
/// MainWindow.IsRefreshShortcut). A hidden tile must drop out even while it still has real numbers;
/// a Stale tile must still count, since it keeps showing its last real numbers (dimmed) on the main
/// page and should not silently vanish from the tray the moment a fetch goes stale.
/// </summary>
public class MainWindowTrayLinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProviderTileViewModel Tile(string id, ProviderStatus status, bool hidden = false, string? realProviderId = null)
    {
        var tile = new ProviderTileViewModel(id, id, realProviderId);
        tile.Apply(
            new ProviderSnapshot(
                ProviderId: id,
                Windows: [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
                PlanType: null,
                SourceKind: SourceKind.LocalFile,
                FetchedAt: Now,
                DataTimestamp: Now,
                Status: status,
                Error: null),
            Now);
        tile.IsHidden = hidden;
        return tile;
    }

    [Fact]
    public void HiddenTilesDropOutButOkAndStaleTilesBothContribute()
    {
        var tiles = new[]
        {
            Tile("hidden", ProviderStatus.Ok, hidden: true),
            Tile("claude", ProviderStatus.Ok),
            Tile("codex", ProviderStatus.Stale),
        };

        var lines = MainWindow.BuildTrayLines(tiles);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.DisplayName == "claude");
        Assert.Contains(lines, l => l.DisplayName == "codex");
    }

    [Fact]
    public void TwoAccountsOfTheSameProviderGetTwoDifferentTrayLines()
    {
        var tiles = new[]
        {
            Tile("claude", ProviderStatus.Ok),
            Tile("claude#2", ProviderStatus.Ok, realProviderId: "claude"),
        };

        var lines = MainWindow.BuildTrayLines(tiles);

        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].DisplayName, lines[1].DisplayName);
    }

    [Fact]
    public void ASecondCallWithNothingChangedSkipsTheTraySummary()
    {
        var tiles = new[] { Tile("claude", ProviderStatus.Ok) };
        var memo = new TrayTooltipMemo();

        var firstChanged = MainWindow.TryComputeTraySummary(tiles, memo, out var firstText, out _, out _);
        var secondChanged = MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _);

        Assert.True(firstChanged);
        Assert.False(secondChanged);
        Assert.False(string.IsNullOrEmpty(firstText));
    }

    [Fact]
    public void AChangedTileReportsChangeAgainAfterAnUnchangedCall()
    {
        var tiles = new List<ProviderTileViewModel> { Tile("claude", ProviderStatus.Ok) };
        var memo = new TrayTooltipMemo();
        MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _);
        MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _);

        tiles.Add(Tile("codex", ProviderStatus.Ok));
        var changed = MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _);

        Assert.True(changed);
    }

    private static ProviderTileViewModel TileAt(string id, double percent)
    {
        var tile = new ProviderTileViewModel(id, id, null);
        tile.Apply(
            new ProviderSnapshot(
                ProviderId: id,
                Windows: [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, Now.AddHours(2), 300)],
                PlanType: null,
                SourceKind: SourceKind.LocalFile,
                FetchedAt: Now,
                DataTimestamp: Now,
                Status: ProviderStatus.Ok,
                Error: null),
            Now);
        return tile;
    }

    [Fact]
    public void OnAutomaticTheTrayIconShowsTheHighestUsage()
    {
        var tiles = new[] { TileAt("claude", 20), TileAt("codex", 55) };

        MainWindow.TryComputeTraySummary(tiles, new TrayTooltipMemo(), out _, out _, out var percent, trayProvider: "Auto");

        Assert.Equal(55, percent);
    }

    [Fact]
    public void AChosenProviderIsShownInTheTrayIconEvenWhenAnotherIsHigher()
    {
        var tiles = new[] { TileAt("claude", 20), TileAt("codex", 55) };

        MainWindow.TryComputeTraySummary(tiles, new TrayTooltipMemo(), out _, out _, out var percent, trayProvider: "claude");

        Assert.Equal(20, percent);
    }

    [Fact]
    public void AChosenProviderWithoutNumbersFallsBackToTheHighestUsage()
    {
        var tiles = new[] { TileAt("claude", 20), TileAt("codex", 55) };

        MainWindow.TryComputeTraySummary(tiles, new TrayTooltipMemo(), out _, out _, out var percent, trayProvider: "gemini");

        Assert.Equal(55, percent);
    }

    [Fact]
    public void ChangingTheChosenProviderRefreshesTheIconEvenThoughTheTooltipStaysTheSame()
    {
        var tiles = new[] { TileAt("claude", 20), TileAt("codex", 55) };
        var memo = new TrayTooltipMemo();
        MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _, trayProvider: "Auto");

        var changed = MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out var percent, trayProvider: "claude");

        Assert.True(changed);
        Assert.Equal(20, percent);
    }

    private static ProviderTileViewModel TileWithBothWindows(string id, double fiveHour, double weekly)
    {
        var tile = new ProviderTileViewModel(id, id, null);
        tile.Apply(
            new ProviderSnapshot(
                ProviderId: id,
                Windows:
                [
                    new UsageWindow("Window_FiveHour", WindowKind.FiveHour, fiveHour, Now.AddHours(2), 300),
                    new UsageWindow("Window_Weekly", WindowKind.Weekly, weekly, Now.AddDays(3), 10080),
                ],
                PlanType: null,
                SourceKind: SourceKind.LocalFile,
                FetchedAt: Now,
                DataTimestamp: Now,
                Status: ProviderStatus.Ok,
                Error: null),
            Now);
        return tile;
    }

    [Theory]
    [InlineData("Auto", 70)]
    [InlineData("FiveHour", 20)]
    [InlineData("Weekly", 70)]
    public void AChosenWindowIsTheOneTheTrayIconShows(string trayWindow, int expected)
    {
        var tiles = new[] { TileWithBothWindows("claude", fiveHour: 20, weekly: 70) };

        MainWindow.TryComputeTraySummary(tiles, new TrayTooltipMemo(), out _, out _, out var percent, "claude", trayWindow);

        Assert.Equal(expected, percent);
    }

    [Fact]
    public void AChosenWindowTheTileDoesNotReportFallsBackToTheHighest()
    {
        var tiles = new[] { TileAt("claude", 20), TileAt("codex", 55) };

        MainWindow.TryComputeTraySummary(tiles, new TrayTooltipMemo(), out _, out _, out var percent, "Auto", "Weekly");

        Assert.Equal(55, percent);
    }

    [Fact]
    public void ChangingTheChosenWindowRefreshesTheIcon()
    {
        var tiles = new[] { TileWithBothWindows("claude", fiveHour: 20, weekly: 70) };
        var memo = new TrayTooltipMemo();
        MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out _, "claude", "Auto");

        var changed = MainWindow.TryComputeTraySummary(tiles, memo, out _, out _, out var percent, "claude", "FiveHour");

        Assert.True(changed);
        Assert.Equal(20, percent);
    }
}
