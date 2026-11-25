using AiUsage.Models;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public class SettingsExportCopyTests
{
    [Fact]
    public void The_export_copy_drops_this_pcs_extra_accounts_hints_and_sign_outs()
    {
        var settings = new AppSettings
        {
            TrayHintShown = true, WelcomeShown = true, ClickThroughHintShown = true,
            LastUpdateCheckUtc = DateTimeOffset.UnixEpoch, KnownLatestTag = "v9.0.0", KnownLatestUrl = "https://github.com/x/y/releases/tag/v9.0.0",
        };
        settings.Providers["claude#2"] = new ProviderSettings { AccountName = "Second" };
        settings.Providers["claude"].Disconnected = true;

        var copy = SettingsViewModel.WithoutMachineLocalState(settings);

        Assert.DoesNotContain("claude#2", copy.Providers.Keys);
        Assert.False(copy.Providers["claude"].Disconnected);
        Assert.False(copy.TrayHintShown || copy.WelcomeShown || copy.ClickThroughHintShown);
        Assert.Null(copy.LastUpdateCheckUtc);
        Assert.Null(copy.KnownLatestTag);
        Assert.Null(copy.KnownLatestUrl);
        Assert.Contains("claude#2", settings.Providers.Keys);
        Assert.True(settings.Providers["claude"].Disconnected);
    }
}
