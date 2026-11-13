using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void AStoredTrayWindowTheProviderNoLongerReportsSelectsTheNextWindowAndKeepsTheStoredOne()
    {
        var settings = new AppSettings { TrayProvider = "cursor", TrayWindow = "Weekly" };
        var providers = new List<IUsageProvider> { new TwoOtherWindowFakeProvider("cursor") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        vm.Tick(Now);

        Assert.Equal("Label:Window_CursorModels", vm.SelectedTrayWindowChoice?.Value);
        Assert.Equal("Weekly", settings.TrayWindow);
    }
}
