using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Views;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ShortcutTests
{
    [Theory]
    [InlineData(Key.T, ModifierKeys.Control, true)]
    [InlineData(Key.T, ModifierKeys.None, false)]
    [InlineData(Key.T, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData(Key.L, ModifierKeys.Control, false)]
    public void StatsShortcutIsOnlyCtrlT(Key key, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, MainWindow.IsStatsShortcut(key, modifiers));

    [Theory]
    [InlineData(Key.L, ModifierKeys.Control, true)]
    [InlineData(Key.L, ModifierKeys.None, false)]
    [InlineData(Key.L, ModifierKeys.Alt, false)]
    [InlineData(Key.T, ModifierKeys.Control, false)]
    public void EyeMenuShortcutIsOnlyCtrlL(Key key, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, MainWindow.IsEyeMenuShortcut(key, modifiers));

    [Fact]
    public void The_main_window_routes_both_shortcuts_to_the_stats_window_and_the_eye_popup()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private void MainWindow_PreviewKeyDown(", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("internal static bool IsRefreshShortcut", start, StringComparison.Ordinal)];

        Assert.Matches(@"IsStatsShortcut\([^)]*\)\)\s*\{\s*TitleBarControl_StatsRequested\(", body);
        Assert.Matches(@"IsEyeMenuShortcut\([^)]*\)\)\s*\{\s*TitleBarControl\.OpenEyeMenu\(\)", body);
    }

    [Fact]
    public void The_eye_popup_opens_from_the_keyboard_and_stays_shut_where_the_button_is_hidden()
    {
        var (shown, hidden) = OnSta(() =>
        {
            var visible = new TitleBar();
            visible.OpenEyeMenu();
            var hiddenBar = new TitleBar { ShowEyeMenu = false };
            hiddenBar.OpenEyeMenu();
            return (Equals(visible.EyePopup.ReadLocalValue(Popup.IsOpenProperty), true),
                    Equals(hiddenBar.EyePopup.ReadLocalValue(Popup.IsOpenProperty), true));
        });

        Assert.True(shown);
        Assert.False(hidden);
    }

    [Fact]
    public void Tooltips_carry_the_shortcut_in_the_active_language_when_hints_are_on()
    {
        var loc = LocalizationService.Instance;
        try
        {
            loc.SetLanguage("de");
            var german = OnSta(ReadTips);
            loc.SetLanguage("en");
            var english = OnSta(ReadTips);

            Assert.Equal(["Tokenverbrauch (Strg+T)", "Einstellungen (Strg+,)", "Anbieter und Darstellung (Strg+L)"], german.WithHints);
            Assert.Equal(["Token usage (Ctrl+T)", "Settings (Ctrl+,)", "Providers and layout (Ctrl+L)"], english.WithHints);
            Assert.Equal(["Token usage", "Settings", "Providers and layout"], english.WithoutHints);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void The_shortcut_helpers_name_the_modifiers_per_language()
    {
        var loc = LocalizationService.Instance;
        try
        {
            loc.SetLanguage("de");
            Assert.Equal("Strg+Alt+U", TitleBar.Shortcut(ModifierKeys.Control | ModifierKeys.Alt, "U"));
            Assert.Equal("Alt+X", TitleBar.Shortcut(ModifierKeys.Alt, "X"));
            Assert.Equal("Aktualisieren (F5)", TitleBar.WithShortcut("Aktualisieren", "F5"));
            loc.SetLanguage("en");
            Assert.Equal("Ctrl+Alt+U", TitleBar.Shortcut(ModifierKeys.Control | ModifierKeys.Alt, "U"));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void The_readme_lists_every_shortcut_the_code_handles()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "README.md"));
        var start = readme.IndexOf("## Keyboard shortcuts", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no keyboard shortcuts section");
        Assert.True(start > readme.IndexOf("## Token usage window", StringComparison.Ordinal));
        var end = readme.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var section = readme[start..end];

        // The default global key comes from the settings model, so a changed default fails here.
        var expected = new[]
        {
            "F5", "Ctrl+,", "Ctrl+T", "Ctrl+L", "Alt+Space", new AppSettings().Hotkey,
            "Ctrl+Alt+Left", "Ctrl+Alt+Right", "Ctrl+Alt+Up", "Ctrl+Alt+Down", "Esc", "Alt+Up", "Alt+Down",
        };
        foreach (var keys in expected)
            Assert.Contains(keys, section, StringComparison.Ordinal);
    }

    private static (string[] WithHints, string[] WithoutHints) ReadTips()
    {
        var with = new TitleBar { ShowShortcutHints = true };
        var without = new TitleBar();
        return ([Tip(with.StatsButton), Tip(with.SettingsButton), Tip(with.EyeButton)],
                [Tip(without.StatsButton), Tip(without.SettingsButton), Tip(without.EyeButton)]);
    }

    private static string Tip(FrameworkElement element) => (string)element.ToolTip;

    private static T OnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = RunWithApp(work);
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
            throw new TimeoutException("shortcut test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("shortcut test failed.", failure);
        return result!;
    }

    private static T RunWithApp<T>(Func<T> work)
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, false);
        var app = new App();
        app.InitializeComponent();
        try
        {
            return work();
        }
        finally
        {
            Application.Current?.Shutdown();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, null);
        }
    }
}
