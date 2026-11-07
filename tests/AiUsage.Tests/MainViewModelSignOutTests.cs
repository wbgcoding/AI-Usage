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

    [Fact]
    public async Task Sign_out_deletes_the_profile_folder_off_the_calling_thread()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        // A slow delete only completes once the caller got control back; a delete on the calling
        // thread would still be holding it here and time out.
        using var callerFree = new ManualResetEventSlim();
        var ranWhileCallerFree = false;
        vm.SignOutWebView = _ =>
        {
            ranWhileCallerFree = callerFree.Wait(TimeSpan.FromSeconds(5));
            return SignOutResult.Removed;
        };

        var signOut = vm.SignOutFromUiAsync("claude");
        callerFree.Set();
        await signOut;

        Assert.True(ranWhileCallerFree);
    }
}
