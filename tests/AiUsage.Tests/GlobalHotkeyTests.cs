using System.Windows.Input;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class GlobalHotkeyTests
{
    [Fact]
    public void TryParse_accepts_a_valid_combination()
    {
        var ok = GlobalHotkey.TryParse("Ctrl+Alt+U", out var modifiers, out var key);

        Assert.True(ok);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, modifiers);
        Assert.Equal(Key.U, key);
    }

    [Fact]
    public void TryParse_rejects_an_unknown_key()
    {
        var ok = GlobalHotkey.TryParse("Ctrl+Alt+NotAKey", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_rejects_a_bare_key_with_no_modifier()
    {
        var ok = GlobalHotkey.TryParse("U", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_rejects_a_shift_only_combination()
    {
        Assert.False(GlobalHotkey.TryParse("Shift+A", out _, out _));
    }

    [Fact]
    public void TryParse_accepts_Ctrl_Shift_and_Alt_alone_as_the_required_modifier()
    {
        Assert.True(GlobalHotkey.TryParse("Ctrl+Shift+A", out _, out _));
        Assert.True(GlobalHotkey.TryParse("Alt+U", out _, out _));
        Assert.True(GlobalHotkey.TryParse("Win+U", out _, out _));
    }

    [Fact]
    public void TryParse_accepts_Shift_alone_with_a_function_key()
    {
        Assert.True(GlobalHotkey.TryParse("Shift+F9", out var modifiers, out var key));
        Assert.Equal(ModifierKeys.Shift, modifiers);
        Assert.Equal(Key.F9, key);
        Assert.True(GlobalHotkey.TryParse("Shift+F24", out _, out _));
        Assert.False(GlobalHotkey.TryParse("F9", out _, out _));
    }

    [Fact]
    public void HasRequiredModifier_needs_Control_Alt_or_Windows_unless_Shift_meets_a_function_key()
    {
        Assert.False(GlobalHotkey.HasRequiredModifier(ModifierKeys.None, Key.A));
        Assert.False(GlobalHotkey.HasRequiredModifier(ModifierKeys.None, Key.F9));
        Assert.False(GlobalHotkey.HasRequiredModifier(ModifierKeys.Shift, Key.A));
        Assert.True(GlobalHotkey.HasRequiredModifier(ModifierKeys.Shift, Key.F9));
        Assert.True(GlobalHotkey.HasRequiredModifier(ModifierKeys.Shift, Key.F24));
        Assert.True(GlobalHotkey.HasRequiredModifier(ModifierKeys.Shift | ModifierKeys.Control, Key.A));
        Assert.True(GlobalHotkey.HasRequiredModifier(ModifierKeys.Alt, Key.A));
    }

    [Fact]
    public void TryParse_rejects_an_empty_string()
    {
        var ok = GlobalHotkey.TryParse("", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Format_is_the_inverse_of_TryParse()
    {
        var text = GlobalHotkey.Format(ModifierKeys.Control | ModifierKeys.Shift, Key.F9);

        var ok = GlobalHotkey.TryParse(text, out var modifiers, out var key);

        Assert.True(ok);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, modifiers);
        Assert.Equal(Key.F9, key);
    }

    [Fact]
    public void An_unrelated_hotkey_id_matches_no_snap_direction()
    {
        var matched = GlobalHotkey.TryMatchSnapDirection(new IntPtr(0x1234), out _);

        Assert.False(matched);
    }

    [Theory]
    [InlineData(GlobalHotkey.SnapLeftId, SnapDirection.Left)]
    [InlineData(GlobalHotkey.SnapRightId, SnapDirection.Right)]
    [InlineData(GlobalHotkey.SnapUpId, SnapDirection.Up)]
    [InlineData(GlobalHotkey.SnapDownId, SnapDirection.Down)]
    public void Each_snap_hotkey_id_matches_its_own_direction(int id, SnapDirection expected)
    {
        var matched = GlobalHotkey.TryMatchSnapDirection(new IntPtr(id), out var direction);

        Assert.True(matched);
        Assert.Equal(expected, direction);
    }
}
