using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Layout checks on the real <see cref="SettingsWindow"/>, built and measured off screen
/// on an STA thread (never shown).</summary>
[Collection(SharedStateTestsCollection.Name)]
public class SettingsWindowLayoutTests
{
    [Fact]
    public void Scrolling_the_content_leaves_the_category_list_where_it_is()
    {
        RunOnSta(window =>
        {
            var root = (UIElement)window.Content;
            root.Measure(new Size(560, 300));
            root.Arrange(new Rect(0, 0, 560, 300));
            root.UpdateLayout();

            var before = window.CategoryList.TranslatePoint(new Point(0, 0), root);
            Assert.True(window.ContentScroll.ScrollableHeight > 0, "the content must be taller than the viewport for this check");

            window.ContentScroll.ScrollToEnd();
            root.UpdateLayout();

            Assert.True(window.ContentScroll.VerticalOffset > 0);
            var after = window.CategoryList.TranslatePoint(new Point(0, 0), root);
            Assert.Equal(before, after);
        });
    }

    private static void RunOnSta(Action<SettingsWindow> check)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
                var app = new AiUsage.App();
                app.InitializeComponent();
                try
                {
                    ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

                    var settingsStore = new SettingsStore(TestPaths.CreateDirectory("settings-window-layout"));
                    var settings = new AppSettings();
                    var mainViewModel = new MainViewModel(settingsStore, settings);
                    var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
                        isAutostartEnabled: () => false,
                        enableAutostart: _ => true,
                        disableAutostart: () => true,
                        applyLanguage: _ => { },
                        loadThemeDictionary: LoadFromProductionAssembly,
                        fetchLatestRelease: _ => Task.FromResult<UpdateCheck.Release?>(null));

                    check(new SettingsWindow(settingsViewModel));
                }
                finally
                {
                    Application.Current?.Shutdown();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    ResetApplicationCurrent();
                }
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

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("SettingsWindow layout walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("SettingsWindow layout walk failed.", failure);
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}
