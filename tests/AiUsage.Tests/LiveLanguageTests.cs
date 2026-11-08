using System.Threading;
using System.Windows.Automation;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Windows and controls that fill texts from code follow a language switch while open.</summary>
[Collection(SharedStateTestsCollection.Name)]
public sealed class LiveLanguageTests
{
    [Fact]
    public void A_welcome_row_rewrites_its_hint_when_the_language_changes()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var tile = new ProviderTileViewModel("gemini", "Gemini");
            tile.Apply(new ProviderSnapshot("gemini", [], null, SourceKind.None, now, null, ProviderStatus.NoLocalData, null), now);
            var row = new WelcomeRow(tile);
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var english = row.Hint;

            loc.SetLanguage("de");

            Assert.NotEqual(english, row.Hint);
            Assert.Equal(loc["Welcome.GeminiHint"], row.Hint);
            Assert.Contains(nameof(WelcomeRow.Hint), raised);
            row.Dispose();
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void A_disposed_welcome_row_ignores_a_language_change()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var tile = new ProviderTileViewModel("gemini", "Gemini");
            tile.Apply(new ProviderSnapshot("gemini", [], null, SourceKind.None, now, null, ProviderStatus.NoLocalData, null), now);
            var row = new WelcomeRow(tile);
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            row.Dispose();

            loc.SetLanguage("de");

            Assert.Empty(raised);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void The_heatmap_accessible_name_follows_a_changed_empty_text()
    {
        var name = OnSta(() =>
        {
            var heatmap = new StatsHeatmap { Values = new long[168], EmptyText = "No data" };
            heatmap.EmptyText = "Keine Daten";
            return AutomationProperties.GetName(heatmap);
        });

        Assert.Equal("Keine Daten", name);
    }

    [Fact]
    public void The_settings_window_listens_to_the_update_notice_and_lets_go_on_close()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "SettingsWindow.xaml.cs"));

        Assert.Contains("_viewModel.Main.Update.PropertyChanged += OnUpdateNoticeChanged;", source);
        Assert.Contains("_viewModel.Main.Update.PropertyChanged -= OnUpdateNoticeChanged;", source);
    }

    [Fact]
    public void The_sign_in_window_listens_to_the_language_and_lets_go_on_close()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "SignInWindow.xaml.cs"));

        Assert.Contains("LocalizationService.Instance.PropertyChanged += OnLanguageChanged;", source);
        Assert.Contains("LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;", source);
    }

    private static T OnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "work did not finish");
        if (failure is not null)
            throw new InvalidOperationException("STA work failed.", failure);
        return result!;
    }
}
