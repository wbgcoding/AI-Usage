using AiUsage.Models;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class LevelAnnouncementTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, 70, "A11y.Level.Warn")] // normal to yellow
    [InlineData(60, 61, "A11y.Level.Warn")] // just over the yellow edge
    [InlineData(70, 90, "A11y.Level.Critical")] // yellow to red
    [InlineData(30, 90, "A11y.Level.Critical")] // straight to red
    [InlineData(90, 100, "A11y.Level.Full")] // red to full
    [InlineData(30, 100, "A11y.Level.Full")] // anything to full
    [InlineData(99, 99.6, "A11y.Level.Full")]
    [InlineData(30, 40, null)] // rise inside a level
    [InlineData(70, 80, null)]
    [InlineData(90, 95, null)]
    [InlineData(100, 100, null)] // stays full
    [InlineData(70, 30, null)] // falling levels say nothing
    [InlineData(100, 90, null)]
    [InlineData(90, 70, null)]
    public void TheDecisionTable(double before, double after, string? expected)
    {
        Assert.Equal(expected, UsageRowViewModel.AnnouncementKeyFor(before, after));
    }

    private static ProviderSnapshot Snapshot(double percent) => new(
        "claude", [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, Now.AddHours(2), 300)],
        "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);

    [Fact]
    public void ARiseOfAShownWindowIsAnnouncedWithProviderWindowAndPercent()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var heard = new List<string>();
        tile.LevelAnnounced += heard.Add;

        tile.Apply(Snapshot(30), Now); // first sight: nothing to compare with
        tile.Apply(Snapshot(70), Now);
        tile.Apply(Snapshot(72), Now); // same level
        tile.Apply(Snapshot(40), Now); // falls

        var text = Assert.Single(heard);
        Assert.Contains("Claude", text);
        Assert.Contains("70", text);
    }

    [Fact]
    public void AHiddenTileAnnouncesNothing()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { IsHidden = true };
        var heard = new List<string>();
        tile.LevelAnnounced += heard.Add;

        tile.Apply(Snapshot(30), Now);
        tile.Apply(Snapshot(95), Now);

        Assert.Empty(heard);
    }
}
