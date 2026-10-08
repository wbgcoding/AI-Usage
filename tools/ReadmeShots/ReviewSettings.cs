// The settings window for the review set: rendered offscreen (never shown) at its normal width,
// once per category that review work touches, in the theme and language the caller has applied.

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
    // The first entry is the default picture (settings-<stem>.png); the others carry their category.
    private static readonly string[] Categories = ["Notifications", "Display", "Updates", "System"];

    internal static void Render(string dataDirectory, string reviewDirectory, string stem)
    {
        ResourceDictionary Load(Uri relative) =>
            new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relative.OriginalString, UriKind.Absolute) };

        var now = DateTimeOffset.Now;
        var samples = SampleProviders.BuildReview(now);
        var settings = new AppSettings();
        var settingsStore = new SettingsStore(dataDirectory);
        var historyStore = new HistoryStore(dataDirectory, () => now);
        var providers = samples.Select(sample => (IUsageProvider)new SampleProviders.Provider(sample)).ToList();
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);

        // Fakes keep the run away from the real Run key and the process-wide language.
        var viewModel = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: Load,
            historyStore: historyStore);

        try
        {
            var window = new SettingsWindow(viewModel);
            var root = (FrameworkElement)window.Content;
            var width = viewModel.SettingsWidth;

            foreach (var category in Categories)
            {
                viewModel.SelectedCategory = category;
                Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
                root.Measure(new Size(width, double.PositiveInfinity));
                Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
                root.Measure(new Size(width, double.PositiveInfinity));
                var height = Math.Ceiling(root.DesiredSize.Height);

                var suffix = category == Categories[0] ? "" : "-" + category;
                Program.RenderToPng(root, width, height, Path.Combine(reviewDirectory, $"settings-{stem}{suffix}.png"));
            }

            // The System page again while a data folder move is running (button disabled, status text next to it).
            viewModel.SelectedCategory = "System";
            viewModel.IsMovingData = true;
            Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
            root.Measure(new Size(width, double.PositiveInfinity));
            Program.RenderToPng(root, width, Math.Ceiling(root.DesiredSize.Height),
                Path.Combine(reviewDirectory, $"settings-{stem}-SystemMoving.png"));
        }
        finally
        {
            viewModel.Dispose();
            main.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
