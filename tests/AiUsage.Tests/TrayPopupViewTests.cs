using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsage.Services;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The tray popup's look: the content view with its automation ids, and the window's flags that
/// keep it from taking the focus. Nothing here shows a window.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class TrayPopupViewTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Local));

    private static TrayPopupModel Model(DateTimeOffset? pausedUntil = null) => TrayPopupModel.Build(
        [
            new TrayTooltipBuilder.ProviderLine("Claude", 42, 63, null, Now.AddHours(3), Now.AddDays(2), "claude"),
            new TrayTooltipBuilder.ProviderLine("Codex", 10, null, null, null, null, "codex"),
        ],
        pausedUntil, Now);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static string[] AutomationIds(DependencyObject root) =>
        [.. Descendants(root).Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0)];

    private static TrayPopupView Laid(TrayPopupModel model)
    {
        var view = new TrayPopupView { DataContext = model };
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public void The_root_and_every_provider_block_carry_an_automation_id()
    {
        var ids = StaTestRunner.Run(() => AutomationIds(Laid(Model())));

        Assert.Contains("TrayPopup", ids);
        Assert.Contains("TrayPopup.Provider.claude", ids);
        Assert.Contains("TrayPopup.Provider.codex", ids);
    }

    [Fact]
    public void The_pause_notice_is_part_of_the_content_while_a_pause_runs()
    {
        var shown = StaTestRunner.Run(() =>
        {
            var view = Laid(Model(Now.AddHours(1)));
            var paused = Descendants(view).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "TrayPopupPaused");
            return (paused.Visibility, paused.Text);
        });

        Assert.Equal(Visibility.Visible, shown.Visibility);
        Assert.False(string.IsNullOrEmpty(shown.Text));
    }

    [Fact]
    public void Without_a_pause_the_notice_is_collapsed()
    {
        var visibility = StaTestRunner.Run(() =>
            Descendants(Laid(Model())).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "TrayPopupPaused").Visibility);

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    [Fact]
    public void The_view_is_wider_than_zero_and_every_reset_text_is_shown_without_any_length_cap()
    {
        var (size, texts) = StaTestRunner.Run(() =>
        {
            var view = Laid(TrayPopupModel.Build(
                Enumerable.Range(1, 8).Select(i => new TrayTooltipBuilder.ProviderLine(
                    "Provider " + i, 40 + i, 50 + i, null, Now.AddHours(i), Now.AddDays(i), "p" + i)).ToList(),
                now: Now));
            return (view.DesiredSize, Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToArray());
        });

        Assert.True(size.Width > 0 && size.Height > 0);
        Assert.Equal(16, texts.Count(t => t.StartsWith("· ", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_window_never_takes_the_focus_and_has_no_taskbar_entry()
    {
        var flags = StaTestRunner.Run(() =>
        {
            var window = new TrayPopupWindow();
            return (window.ShowInTaskbar, window.ShowActivated, window.Topmost, window.Focusable, window.IsShown,
                window.WindowStyle, window.Content is TrayPopupView);
        });

        Assert.False(flags.ShowInTaskbar);
        Assert.False(flags.ShowActivated);
        Assert.True(flags.Topmost);
        Assert.False(flags.Focusable);
        Assert.False(flags.IsShown);
        Assert.Equal(WindowStyle.None, flags.WindowStyle);
        Assert.True(flags.Item7);
    }

    [Fact]
    public void A_placement_is_logged_once_until_it_changes()
    {
        var answers = StaTestRunner.Run(() =>
        {
            var window = new TrayPopupWindow();
            return new[]
            {
                window.ShouldLog("Tray popup: placed 10,20"),
                window.ShouldLog("Tray popup: placed 10,20"),
                window.ShouldLog("Tray popup: placed 30,20"),
                window.ShouldLog("Tray popup: placed 10,20"),
            };
        });

        Assert.Equal([true, false, true, true], answers);
    }
}
