using AiUsage.Models;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public async Task SignOutFromUiAsync_marks_the_tile_when_the_profile_could_not_be_cleared()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        vm.SignOutWebView = _ => SignOutResult.StillOpen;

        await vm.SignOutFromUiAsync("claude");

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.Blocked, tile.Status);
        Assert.False(vm.IsSigningOut("claude"));
    }
}
