using AiUsage.Services;
using AiUsage.Views;

namespace AiUsage.Tests;

public class SnapHotkeyRegistrationTests
{
    private sealed class FakeSystem
    {
        public HashSet<int> Registered { get; } = [];
        public int RegisterCalls { get; private set; }

        public bool Register(IntPtr hwnd, int id, uint modifiers, uint vk)
        {
            RegisterCalls++;
            return Registered.Add(id);
        }

        public void Unregister(IntPtr hwnd, int id) => Registered.Remove(id);
    }

    private static (GlobalHotkey Hotkey, FakeSystem System) Create()
    {
        var system = new FakeSystem();
        return (new GlobalHotkey(new IntPtr(1), system.Register, system.Unregister), system);
    }

    [Fact]
    public void Nothing_is_registered_until_the_widget_is_active()
    {
        var (hotkey, system) = Create();

        Assert.False(hotkey.SnapHotkeysRegistered);
        Assert.Empty(system.Registered);
    }

    [Fact]
    public void Activating_registers_all_four_snap_keys()
    {
        var (hotkey, system) = Create();

        hotkey.SetSnapHotkeysActive(true);

        Assert.True(hotkey.SnapHotkeysRegistered);
        Assert.Equal(
            [GlobalHotkey.SnapLeftId, GlobalHotkey.SnapRightId, GlobalHotkey.SnapUpId, GlobalHotkey.SnapDownId],
            system.Registered.Order());
    }

    [Fact]
    public void Deactivating_releases_all_four_snap_keys()
    {
        var (hotkey, system) = Create();
        hotkey.SetSnapHotkeysActive(true);

        hotkey.SetSnapHotkeysActive(false);

        Assert.False(hotkey.SnapHotkeysRegistered);
        Assert.Empty(system.Registered);
    }

    [Fact]
    public void Activating_twice_registers_once()
    {
        var (hotkey, system) = Create();

        hotkey.SetSnapHotkeysActive(true);
        hotkey.SetSnapHotkeysActive(true);

        Assert.Equal(4, system.RegisterCalls);
    }

    [Fact]
    public void Activating_again_after_a_release_registers_again()
    {
        var (hotkey, system) = Create();
        hotkey.SetSnapHotkeysActive(true);
        hotkey.SetSnapHotkeysActive(false);

        hotkey.SetSnapHotkeysActive(true);

        Assert.Equal(4, system.Registered.Count);
    }

    [Fact]
    public void Dispose_releases_the_snap_keys()
    {
        var (hotkey, system) = Create();
        hotkey.SetSnapHotkeysActive(true);

        hotkey.Dispose();

        Assert.Empty(system.Registered);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void The_snap_keys_are_wanted_only_while_active_and_visible(bool active, bool visible, bool expected)
    {
        Assert.Equal(expected, MainWindow.SnapHotkeysWanted(active, visible));
    }

    [Fact]
    public void SnapToHalf_returns_at_once_while_the_window_is_not_visible()
    {
        var source = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.xaml.cs");
        var body = SourceScan.Member(source, "private void SnapToHalf(SnapDirection direction)");

        Assert.Contains("!IsVisible", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_syncs_the_snap_keys_on_activation_deactivation_and_visibility()
    {
        var source = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.xaml.cs");

        Assert.Contains("Activated += (_, _) => SyncSnapHotkeys()", source, StringComparison.Ordinal);
        Assert.Contains("Deactivated += (_, _) => SyncSnapHotkeys()", source, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged += (_, _) => SyncSnapHotkeys()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_globalHotkey.RegisterSnapHotkeys()", source, StringComparison.Ordinal);
    }
}
