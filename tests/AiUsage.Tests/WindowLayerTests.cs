using System.Runtime.InteropServices;
using System.Windows.Controls;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class WindowLayerTests
{
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"alwaysOnTop\":true}", WindowLayers.OnTop)]
    [InlineData("{\"schemaVersion\":1,\"AlwaysOnTop\":true}", WindowLayers.OnTop)]
    [InlineData("{\"schemaVersion\":1,\"alwaysOnTop\":false}", WindowLayers.Normal)]
    [InlineData("{\"schemaVersion\":1}", WindowLayers.Normal)]
    [InlineData("{\"schemaVersion\":1,\"windowLayer\":\"Desktop\"}", WindowLayers.Desktop)]
    [InlineData("{\"schemaVersion\":1,\"windowLayer\":\"Desktop\",\"alwaysOnTop\":true}", WindowLayers.Desktop)]
    [InlineData("{\"schemaVersion\":1,\"windowLayer\":\"Normal\",\"alwaysOnTop\":true}", WindowLayers.Normal)]
    [InlineData("{\"schemaVersion\":1,\"windowLayer\":\"Sideways\"}", WindowLayers.Normal)]
    public void A_loaded_file_resolves_to_one_window_level(string json, string expected)
    {
        var (settings, refusal) = SettingsStore.Validate(json);

        Assert.Null(refusal);
        Assert.Equal(expected, settings!.WindowLayer);
        Assert.Null(settings.LegacyAlwaysOnTop);
    }

    [Fact]
    public void The_old_flag_is_never_written_back_and_the_level_survives_a_round_trip()
    {
        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-window-layer");
        using var store = new SettingsStore(directory);
        var (migrated, _) = SettingsStore.Validate("{\"schemaVersion\":1,\"alwaysOnTop\":true}");

        store.SaveNow(migrated!);
        var json = File.ReadAllText(Path.Combine(directory, "settings.json"));

        Assert.DoesNotContain("alwaysOnTop", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"windowLayer\": \"OnTop\"", json, StringComparison.Ordinal);
        using var reload = new SettingsStore(directory);
        Assert.Equal(WindowLayers.OnTop, reload.Load().WindowLayer);
    }

    [Fact]
    public void A_fresh_install_starts_with_a_normal_window()
    {
        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-window-layer");
        using var store = new SettingsStore(directory);

        Assert.Equal(WindowLayers.Normal, store.Load().WindowLayer);
    }

    [Theory]
    [InlineData("OnTop", "OnTop")]
    [InlineData("desktop", "Desktop")]
    [InlineData(null, "Normal")]
    [InlineData("", "Normal")]
    [InlineData("Floating", "Normal")]
    public void A_stored_value_is_normalized_to_a_known_level(string? stored, string expected) =>
        Assert.Equal(expected, WindowLayers.Normalize(stored));

    [Fact]
    public void Click_through_forces_the_on_top_level_even_from_the_desktop_level_and_leaves_it_after()
    {
        var policy = new ClickThroughPolicy();

        var on = policy.Resolve(clickThrough: true, windowLayer: WindowLayers.Desktop, opacity: 90);
        Assert.Equal(WindowLayers.OnTop, on.WindowLayer);

        var off = policy.Resolve(clickThrough: false, windowLayer: WindowLayers.Desktop, opacity: 90);
        Assert.Equal(WindowLayers.Desktop, off.WindowLayer);
    }

    private static readonly IntPtr Self = new(100);
    private static DesktopLayer.StackEntry Window(int handle) => new(new IntPtr(handle), false, false);
    private static DesktopLayer.StackEntry Topmost(int handle) => new(new IntPtr(handle), false, true);
    private static DesktopLayer.StackEntry Desktop(int handle) => new(new IntPtr(handle), true, false);

    [Fact]
    public void The_desktop_slot_is_directly_above_the_desktop_window()
    {
        var slot = DesktopLayer.ResolveSlot([Topmost(1), Window(2), Window(3), Desktop(9), Window(8)], Self);

        Assert.False(slot.InPlace);
        Assert.Equal(new IntPtr(3), slot.InsertAfter);
    }

    [Fact]
    public void A_window_already_directly_above_the_desktop_is_in_place()
    {
        var slot = DesktopLayer.ResolveSlot([Topmost(1), Window(2), new(Self, false, false), Desktop(9)], Self);

        Assert.True(slot.InPlace);
    }

    [Fact]
    public void A_window_below_the_desktop_is_lifted_to_just_above_it()
    {
        var slot = DesktopLayer.ResolveSlot([Window(2), Desktop(9), new(Self, false, false)], Self);

        Assert.False(slot.InPlace);
        Assert.Equal(new IntPtr(2), slot.InsertAfter);
    }

    [Fact]
    public void With_only_topmost_windows_over_the_desktop_the_slot_is_the_top_of_the_ordinary_windows()
    {
        var slot = DesktopLayer.ResolveSlot([Topmost(1), Desktop(9)], Self);

        Assert.False(slot.InPlace);
        Assert.Equal(IntPtr.Zero, slot.InsertAfter);
    }

    [Fact]
    public void Without_a_desktop_window_the_slot_is_the_very_bottom()
    {
        var slot = DesktopLayer.ResolveSlot([Window(2), Window(3)], Self);

        Assert.False(slot.InPlace);
        Assert.Equal(new IntPtr(1), slot.InsertAfter);
    }

    [Fact]
    public void Every_pending_position_change_is_rewritten_to_the_slot_and_a_settled_one_is_left_alone()
    {
        var position = new DesktopLayer.WindowPos { HwndInsertAfter = IntPtr.Zero, Flags = 0x0004 | 0x0010 };
        var slot = new DesktopLayer.Slot(false, new IntPtr(3));

        Assert.True(DesktopLayer.ApplySlot(ref position, slot));
        Assert.Equal(new IntPtr(3), position.HwndInsertAfter);
        Assert.Equal(0u, position.Flags & 0x0004);
        Assert.Equal(0x0010u, position.Flags & 0x0010);
        Assert.False(DesktopLayer.ApplySlot(ref position, slot));
    }

    [Fact]
    public void A_window_in_place_keeps_its_place_whatever_Windows_asked_for()
    {
        var position = new DesktopLayer.WindowPos { HwndInsertAfter = IntPtr.Zero, Flags = 0 };
        var slot = new DesktopLayer.Slot(true, Self);

        Assert.True(DesktopLayer.ApplySlot(ref position, slot));
        Assert.Equal(0x0004u, position.Flags & 0x0004);
        Assert.False(DesktopLayer.ApplySlot(ref position, slot));
    }

    [Fact]
    public void The_position_message_is_rewritten_only_while_the_desktop_level_is_active()
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<DesktopLayer.WindowPos>());
        try
        {
            using var layer = new DesktopLayer(IntPtr.Zero, () => false, native: false);
            Marshal.StructureToPtr(new DesktopLayer.WindowPos { HwndInsertAfter = IntPtr.Zero, Flags = 0x0004 }, pointer, false);

            layer.OnWindowPosChanging(pointer);
            Assert.Equal(IntPtr.Zero, Marshal.PtrToStructure<DesktopLayer.WindowPos>(pointer).HwndInsertAfter);

            layer.SetActive(true);
            layer.OnWindowPosChanging(pointer);
            var rewritten = Marshal.PtrToStructure<DesktopLayer.WindowPos>(pointer);
            Assert.Equal(new IntPtr(1), rewritten.HwndInsertAfter);
            Assert.Equal(0u, rewritten.Flags & 0x0004);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_desktop_windows_count_as_the_desktop(string? className, bool expected) =>
        Assert.Equal(expected, DesktopLayer.IsDesktopWindowClass(className));

    [Theory]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, true, true, false)]
    public void Showing_the_desktop_brings_back_only_a_window_it_minimized(bool active, bool leaveAlone, bool minimized, bool nativeVisible, bool expected) =>
        Assert.Equal(expected, DesktopLayer.ShouldReshow(active, leaveAlone, minimized, nativeVisible));

    [Fact]
    public void The_window_level_menu_checks_exactly_one_entry_and_reports_a_pick()
    {
        var result = StaTestRunner.Run(() =>
        {
            var menu = new WindowLayerMenu(WindowLayers.Normal);
            string? chosen = null;
            menu.Chosen += (_, layer) => chosen = layer;

            menu.Select(WindowLayers.Desktop);
            var afterSelect = CheckedTags(menu.Header);
            var raisedBySelect = chosen;

            var onTop = menu.Header.Items.Cast<MenuItem>().First(i => (string)i.Tag == WindowLayers.OnTop);
            onTop.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

            return new MenuResult(afterSelect, raisedBySelect, chosen, CheckedTags(menu.Header),
                (string)menu.Header.Header, menu.Header.Items.Cast<MenuItem>().Select(i => (string)i.Header).ToArray());
        });

        Assert.Equal(WindowLayers.Desktop, result.CheckedAfterSelect);
        Assert.Null(result.RaisedBySelect);
        Assert.Equal(WindowLayers.OnTop, result.Picked);
        Assert.Equal(WindowLayers.OnTop, result.CheckedAfterPick);
        Assert.False(string.IsNullOrEmpty(result.ParentHeader));
        Assert.Equal(3, result.ChildHeaders.Length);
        Assert.All(result.ChildHeaders, header => Assert.False(string.IsNullOrEmpty(header)));
    }

    [Fact]
    public void The_title_bar_menu_and_the_tray_menu_both_offer_the_three_levels()
    {
        var (titleBarMenu, trayMenu) = StaTestRunner.Run(() =>
        {
            var titleBar = new TitleBar { ShowWindowMenu = true };
            titleBar.SetWindowLayer(WindowLayers.Desktop);
            var tray = TrayService.BuildMenu(WindowLayers.Desktop, clickThrough: false, hotkeyShortcutText: null);
            return (LayerEntries(titleBar.WindowMenu), LayerEntries(tray));
        });

        foreach (var entries in new[] { titleBarMenu, trayMenu })
        {
            Assert.Equal(["OnTop", "Normal", "Desktop"], entries.All);
            Assert.Equal(WindowLayers.Desktop, entries.Checked);
        }
    }

    private static string CheckedTags(MenuItem parent) =>
        string.Join(",", parent.Items.Cast<MenuItem>().Where(i => i.IsChecked).Select(i => (string)i.Tag));

    private static (string[] All, string Checked) LayerEntries(ContextMenu menu)
    {
        var parent = menu.Items.OfType<MenuItem>().Single(item => item.Items.Count == 3);
        return (parent.Items.Cast<MenuItem>().Select(i => (string)i.Tag).ToArray(), CheckedTags(parent));
    }

    private sealed record MenuResult(
        string CheckedAfterSelect, string? RaisedBySelect, string? Picked, string CheckedAfterPick, string ParentHeader, string[] ChildHeaders);
}
