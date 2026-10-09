// The settings window for the review set: rendered offscreen (never shown), one picture per
// category of the view model's own list, in the theme, language and size the caller has applied.

using System.IO;
using System.Windows;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;

namespace ReadmeShots;

internal static class ReviewSettings
{
    /// <summary>The page values of the settings sidebar, read from the view model so a new page
    /// shows up in the review set without a second list here.</summary>
    internal static IReadOnlyList<string> CategoryValues(string dataDirectory)
    {
        using var bundle = new Bundle(dataDirectory);
        return bundle.ViewModel.Categories.Select(choice => choice.Value).ToList();
    }

    internal static void RenderCategory(SurfaceContext context, string id, string category) =>
        Render(context, viewModel => viewModel.SelectedCategory = category, context.OutputPath(id));

    /// <summary>The System page while a data folder move is running (button disabled, status text next
    /// to it).</summary>
    internal static void RenderMoving(SurfaceContext context, string id) =>
        Render(context, viewModel =>
        {
            viewModel.SelectedCategory = "System";
            viewModel.IsMovingData = true;
        }, context.OutputPath(id));

    private static void Render(SurfaceContext context, Action<SettingsViewModel> prepare, string outputPath)
    {
        using var bundle = new Bundle(context.DataDirectory);
        var viewModel = bundle.ViewModel;
        var window = new SettingsWindow(viewModel);
        var root = (FrameworkElement)window.Content;
        var width = context.Pick(viewModel.SettingsWidth, window.MinWidth);

        prepare(viewModel);
        Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
        root.Measure(new Size(width, double.PositiveInfinity));
        Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
        root.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Ceiling(root.DesiredSize.Height);
        Program.RenderToPng(root, width, height, outputPath);
    }

    /// <summary>A settings view model with sample providers behind it, and its disposal.</summary>
    private sealed class Bundle : IDisposable
    {
        private readonly MainViewModel _main;

        internal Bundle(string dataDirectory)
        {
            ResourceDictionary Load(Uri relative) =>
                new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relative.OriginalString, UriKind.Absolute) };

            var now = DateTimeOffset.Now;
            var samples = SampleProviders.BuildReview(now);
            var settings = new AppSettings();
            var settingsStore = new SettingsStore(dataDirectory);
            var historyStore = new HistoryStore(dataDirectory, () => now);
            var providers = samples.Select(sample => (IUsageProvider)new SampleProviders.Provider(sample)).ToList();
            _main = new MainViewModel(settingsStore, settings, providers, historyStore);

            // Fakes keep the run away from the real Run key and the process-wide language.
            ViewModel = new SettingsViewModel(_main, settings, settingsStore,
                isAutostartEnabled: () => false,
                enableAutostart: _ => true,
                disableAutostart: () => true,
                applyLanguage: _ => { },
                loadThemeDictionary: Load,
                historyStore: historyStore);
        }

        internal SettingsViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            _main.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
