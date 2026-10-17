using System.Windows;
using System.Windows.Controls;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>The tray menu: seven top-level entries, no separator lines, the window controls in one submenu.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class TrayMenuTests
{
    private static string Text(string key) => LocalizationService.Instance[key];

    [Fact]
    public void The_top_level_lists_show_hide_stats_settings_refresh_pause_widget_exit_without_separators()
    {
        var headers = StaTestRunner.Run(() =>
        {
            var menu = TrayService.BuildMenu(WindowLayers.Normal, clickThrough: false, hotkeyShortcutText: null);
            Assert.All(menu.Items.Cast<object>(), item => Assert.IsType<MenuItem>(item));
            return menu.Items.Cast<MenuItem>().Select(item => (string)item.Header).ToArray();
        });

        Assert.Equal(
            [Text("Tray.Hide"), Text("Tray.Stats"), Text("Tray.Settings"), Text("Action.RefreshNow"), Text("Tray.Pause"), Text("Tray.Widget"), Text("Tray.Exit")],
            headers);
    }

    [Fact]
    public void The_widget_submenu_holds_the_three_levels_then_click_through_then_reset_position()
    {
        var (headers, tags, checkable) = StaTestRunner.Run(() =>
        {
            var menu = TrayService.BuildMenu(WindowLayers.Desktop, clickThrough: true, hotkeyShortcutText: null);
            var widget = menu.Items.Cast<MenuItem>().Single(item => (string)item.Header == Text("Tray.Widget"));
            Assert.All(widget.Items.Cast<object>(), item => Assert.IsType<MenuItem>(item));
            var items = widget.Items.Cast<MenuItem>().ToList();
            return (items.Select(item => (string)item.Header).ToArray(),
                items.Select(item => item.Tag as string).ToArray(),
                items.Select(item => (item.IsCheckable, item.IsChecked)).ToArray());
        });

        Assert.Equal(5, headers.Length);
        Assert.Equal(new string?[] { WindowLayers.OnTop, WindowLayers.Normal, WindowLayers.Desktop, null, null }, tags);
        Assert.Equal(Text("Tray.ClickThrough"), headers[3]);
        Assert.Equal(Text("Tray.ResetPosition"), headers[4]);
        Assert.Equal((true, true), checkable[2]);
        Assert.Equal((true, true), checkable[3]);
        Assert.False(checkable[4].IsCheckable);
    }

    [Theory]
    [InlineData(true, "Tray.Hide")]
    [InlineData(false, "Tray.Show")]
    public void The_first_entry_is_bold_and_follows_the_widget_visibility(bool widgetVisible, string key)
    {
        var (header, weight, gesture) = StaTestRunner.Run(() =>
        {
            var menu = TrayService.BuildMenu(WindowLayers.Normal, clickThrough: false, hotkeyShortcutText: "Ctrl+Alt+U", widgetVisible: widgetVisible);
            var first = (MenuItem)menu.Items[0];
            return ((string)first.Header, first.FontWeight, first.InputGestureText);
        });

        Assert.Equal(Text(key), header);
        Assert.Equal(FontWeights.SemiBold, weight);
        Assert.Equal(GlobalHotkey.FormatForDisplay("Ctrl+Alt+U"), gesture);
    }
}
