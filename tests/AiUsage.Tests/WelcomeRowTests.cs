using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views;

namespace AiUsage.Tests;

public class WelcomeRowTests
{
    private static ProviderTileViewModel TileWith(string id, ProviderStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var tile = new ProviderTileViewModel(id, id) { SupportsInAppSignIn = true };
        tile.Apply(new ProviderSnapshot(id, [], null, SourceKind.None, now, null, status, null), now);
        return tile;
    }

    [Theory]
    [InlineData(ProviderStatus.Ok, true)]
    [InlineData(ProviderStatus.Stale, true)]
    [InlineData(ProviderStatus.NotSignedIn, false)]
    [InlineData(ProviderStatus.NoLocalData, false)]
    public void A_provider_that_already_reads_is_marked_found_and_offers_no_sign_in(ProviderStatus status, bool found)
    {
        var row = new WelcomeRow(TileWith("claude", status));

        Assert.Equal(found, row.IsFound);
        if (found)
            Assert.False(row.ShowSignIn);
    }

    [Fact]
    public void The_row_flips_to_found_when_the_tile_gets_numbers()
    {
        var tile = TileWith("claude", ProviderStatus.NotSignedIn);
        var row = new WelcomeRow(tile);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var now = DateTimeOffset.UtcNow;
        tile.Apply(new ProviderSnapshot("claude", [], null, SourceKind.None, now, null, ProviderStatus.Ok, null), now);

        Assert.True(row.IsFound);
        Assert.Contains(nameof(WelcomeRow.IsFound), raised);
    }

    [Fact]
    public void Only_gemini_and_copilot_carry_a_hint()
    {
        var loc = LocalizationService.Instance;

        Assert.Equal(loc["Welcome.GeminiHint"], WelcomeRow.HintFor("gemini"));
        Assert.Equal(loc["Welcome.CopilotHint"], WelcomeRow.HintFor("copilot"));
        Assert.Null(WelcomeRow.HintFor("claude"));
        Assert.Null(WelcomeRow.HintFor("codex"));
        Assert.Null(WelcomeRow.HintFor("cursor"));
        Assert.True(new WelcomeRow(TileWith("gemini", ProviderStatus.NotSignedIn)).HasHint);
    }
}
