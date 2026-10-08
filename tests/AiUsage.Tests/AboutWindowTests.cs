using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Builds the real <see cref="SettingsWindow"/> off screen (same STA-thread construction
/// <see cref="StatsWindowTests"/> already documents, never shown, no real dialog loop) once, and
/// proves the About category's read-locations section keeps its own acceptance from the old
/// standalone About window: one collapsible group per known provider, every one collapsed at open,
/// and every path row one the provider itself actually reports through
/// <see cref="IUsageProvider.ReadLocations"/>. Every WPF object the worker thread touches is read
/// into plain records before the thread ends - a <see cref="StackPanel"/> handed back and inspected
/// from the calling thread throws (WPF objects are thread-affine).
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class AboutWindowTests
{
    private sealed record GroupResult(bool IsCollapsedAtOpen, IReadOnlyList<string> ShownPaths);

    [Fact]
    public void EveryKnownProviderGetsItsOwnCollapsedGroupWithItsRealReadLocations()
    {
        Exception? failure = null;
        IReadOnlyList<GroupResult>? results = null;

        var worker = new Thread(() =>
        {
            try
            {
                results = BuildAndReadGroups();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("SettingsWindow build did not finish");

        if (failure is not null)
            throw new InvalidOperationException("SettingsWindow build failed.", failure);

        Assert.NotNull(results);

        // Same order ProviderRegistry.CreateAll always builds them in with default settings: the
        // primary Claude account, then Codex, Cursor, Gemini and Copilot (the tile order).
        IReadOnlyList<IUsageProvider> providers =
        [
            ProviderRegistry.CreatePrimaryClaudeAccount(), new CodexProvider(), new CursorProvider(),
            new GeminiProvider(), new CopilotProvider(),
        ];

        Assert.Equal(providers.Count, results!.Count);
        for (var i = 0; i < providers.Count; i++)
        {
            Assert.True(results[i].IsCollapsedAtOpen, $"provider {providers[i].Id}: group is not collapsed at open");
            Assert.Equal(providers[i].ReadLocations, results[i].ShownPaths);
        }
    }

    [Fact]
    public void AKnownUpdateShowsOnTheAboutPageEvenWhenTheDailyCheckIsNotDue()
    {
        Exception? failure = null;
        (bool PanelVisible, bool InstallVisible)? result = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = BuildWithAKnownUpdate();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("SettingsWindow build did not finish");
        if (failure is not null)
            throw new InvalidOperationException("SettingsWindow build failed.", failure);

        Assert.True(result!.Value.PanelVisible);
        Assert.True(result.Value.InstallVisible);
    }

    [Fact]
    public void ALanguageSwitchWhileTheWindowIsOpenRebuildsTheAboutPage()
    {
        Exception? failure = null;
        (string Before, string After)? result = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = SwitchLanguageWithTheWindowOpen();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("SettingsWindow build did not finish");
        if (failure is not null)
            throw new InvalidOperationException("SettingsWindow build failed.", failure);

        Assert.NotEqual(result!.Value.Before, result.Value.After);
        Assert.StartsWith("Reads:", result.Value.After, StringComparison.Ordinal);
    }

    private static (string Before, string After) SwitchLanguageWithTheWindowOpen()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();
        var loc = LocalizationService.Instance;
        // The suite's ambient language is German; exactly that is put back afterwards.
        loc.SetLanguage("de");

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var settingsStore = new SettingsStore(TestPaths.CreateDirectory("about-window-language"));
            var settings = new AppSettings();
            var mainViewModel = new MainViewModel(settingsStore, settings);
            var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
                isAutostartEnabled: () => false,
                enableAutostart: _ => true,
                disableAutostart: () => true,
                applyLanguage: _ => { },
                loadThemeDictionary: LoadFromProductionAssembly,
                fetchLatestRelease: _ => Task.FromResult<UpdateCheck.Release?>(null));

            var window = new SettingsWindow(settingsViewModel);
            var before = FirstGroupCaption(window);

            loc.SetLanguage("en");

            return (before, FirstGroupCaption(window));
        }
        finally
        {
            loc.SetLanguage("de");
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    // The small caption under the first provider's name in the read-locations list: built from code
    // with a resolved string, so it only follows a language switch if the page is rebuilt.
    private static string FirstGroupCaption(SettingsWindow window)
    {
        var group = (StackPanel)window.AboutReadLocationsPanel.Children[0];
        var header = (Button)group.Children[0];
        var content = (StackPanel)header.Content;
        var texts = (StackPanel)content.Children[1];
        return ((TextBlock)texts.Children[1]).Text;
    }

    private static (bool PanelVisible, bool InstallVisible) BuildWithAKnownUpdate()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var settingsStore = new SettingsStore(TestPaths.CreateDirectory("about-window-update"));
            // Checked a moment ago: the daily check is not due, so it answers without any await.
            var settings = new AppSettings
            {
                KnownLatestTag = "v999.0.0",
                KnownLatestUrl = "https://github.com/wbgcoding/AI-Usage/releases/tag/v999.0.0",
                LastUpdateCheckUtc = DateTimeOffset.UtcNow,
            };
            var mainViewModel = new MainViewModel(settingsStore, settings);
            var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
                isAutostartEnabled: () => false,
                enableAutostart: _ => true,
                disableAutostart: () => true,
                applyLanguage: _ => { },
                loadThemeDictionary: LoadFromProductionAssembly,
                fetchLatestRelease: _ => Task.FromResult<UpdateCheck.Release?>(null));

            var window = new SettingsWindow(settingsViewModel);

            return (window.AboutUpdatePanel.Visibility == Visibility.Visible,
                window.AboutInstallUpdateButton.Visibility == Visibility.Visible);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    // Builds the real window and reads every group's state into plain data, all on this same STA
    // worker thread - the returned list carries no WPF object across the thread boundary.
    private static IReadOnlyList<GroupResult> BuildAndReadGroups()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var settingsStore = new SettingsStore(TestPaths.CreateDirectory("about-window"));
            var settings = new AppSettings();
            var mainViewModel = new MainViewModel(settingsStore, settings);
            var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
                isAutostartEnabled: () => false,
                enableAutostart: _ => true,
                disableAutostart: () => true,
                applyLanguage: _ => { },
                loadThemeDictionary: LoadFromProductionAssembly,
                fetchLatestRelease: _ => Task.FromResult<UpdateCheck.Release?>(null));

            var window = new SettingsWindow(settingsViewModel);
            var panel = window.AboutReadLocationsPanel;

            IReadOnlyList<IUsageProvider> providers =
            [
                ProviderRegistry.CreatePrimaryClaudeAccount(), new CodexProvider(), new CursorProvider(),
                new GeminiProvider(), new CopilotProvider(),
            ];

            var results = new List<GroupResult>();
            for (var i = 0; i < providers.Count; i++)
            {
                var group = Assert.IsType<StackPanel>(panel.Children[i]);
                Assert.IsType<Button>(group.Children[0]);
                var content = Assert.IsType<StackPanel>(group.Children[1]);

                // Only the read-location rows carry the untrimmed entry as their Tag - the nested
                // per-tile diagnostic row(s) that follow them do not, so they are excluded here rather
                // than asserted on by this test.
                var shownPaths = content.Children.OfType<TextBox>()
                    .Where(box => box.Tag is string)
                    .Select(box => (string)box.Tag!).ToList();
                results.Add(new GroupResult(content.Visibility == Visibility.Collapsed, shownPaths));
            }

            return results;
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}
