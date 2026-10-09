using System.Runtime.InteropServices;
using System.Windows.Input;

namespace AiUsage.Services;

/// <summary>
/// Thin RegisterHotKey/UnregisterHotKey (user32) wrapper for the one optional global shortcut that
/// brings the hidden widget back - driven from MainWindow's own HwndSource hook (the same one that
/// already handles WM_EXITSIZEMOVE), never a hook of its own. Registration failing because another
/// program already owns the exact combination is a normal outcome, not an exception: the caller
/// decides what to show for it (Settings.HotkeyTaken).
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    internal const int WM_HOTKEY = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    // Arbitrary but fixed - this app only ever registers one user-configurable global hotkey, on one
    // window.
    private const int HotkeyId = 0x554B;

    // The four snap-to-half shortcuts are fixed (never user-configurable) and always the same
    // combination, so each gets its own arbitrary id rather than sharing HotkeyId. Internal (rather
    // than private) only so WndProc-level tests can exercise TryMatchSnapDirection against a real id.
    internal const int SnapLeftId = 0x554C;
    internal const int SnapRightId = 0x554D;
    internal const int SnapUpId = 0x554E;
    internal const int SnapDownId = 0x554F;

    private const ModifierKeys SnapModifiers = ModifierKeys.Control | ModifierKeys.Alt;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private bool _registered;
    private readonly HashSet<int> _registeredSnapIds = [];

    public GlobalHotkey(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>True once registered; false, without throwing, when another program already owns
    /// this exact combination.</summary>
    public bool Register(ModifierKeys modifiers, Key key)
    {
        Unregister();

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_hwnd, HotkeyId, ToNative(modifiers), vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    /// <summary>Registers all four snap-to-half shortcuts (Ctrl+Alt+Left/Right/Up/Down). Each
    /// direction that fails - another program already owns that exact combination - simply stays
    /// unregistered; the corresponding window-menu entry keeps working regardless, same "fails
    /// quietly" contract as <see cref="Register"/>.</summary>
    public void RegisterSnapHotkeys()
    {
        UnregisterSnapHotkeys();

        TryRegisterSnap(SnapLeftId, Key.Left);
        TryRegisterSnap(SnapRightId, Key.Right);
        TryRegisterSnap(SnapUpId, Key.Up);
        TryRegisterSnap(SnapDownId, Key.Down);
    }

    public void UnregisterSnapHotkeys()
    {
        foreach (var id in _registeredSnapIds)
            UnregisterHotKey(_hwnd, id);
        _registeredSnapIds.Clear();
    }

    private void TryRegisterSnap(int id, Key key)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (RegisterHotKey(_hwnd, id, ToNative(SnapModifiers), vk))
            _registeredSnapIds.Add(id);
    }

    private static uint ToNative(ModifierKeys modifiers)
    {
        // MOD_NOREPEAT: holding the combination must not fire the show/hide toggle over and over.
        uint native = ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt)) native |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= ModWin;
        return native;
    }

    /// <summary>True when a WM_HOTKEY message's wParam names the one user-configurable hotkey this
    /// app registers - checking the id keeps this correct even though only one is ever registered on
    /// this window.</summary>
    internal static bool Matches(IntPtr wParam) => wParam.ToInt32() == HotkeyId;

    /// <summary>The inverse for the four fixed snap shortcuts: which direction (if any) a WM_HOTKEY
    /// message's wParam names.</summary>
    internal static bool TryMatchSnapDirection(IntPtr wParam, out SnapDirection direction)
    {
        switch (wParam.ToInt32())
        {
            case SnapLeftId: direction = SnapDirection.Left; return true;
            case SnapRightId: direction = SnapDirection.Right; return true;
            case SnapUpId: direction = SnapDirection.Up; return true;
            case SnapDownId: direction = SnapDirection.Down; return true;
            default: direction = default; return false;
        }
    }

    public void Dispose()
    {
        Unregister();
        UnregisterSnapHotkeys();
    }

    /// <summary>
    /// Parses "Ctrl+Alt+U"-style text: any of Ctrl/Alt/Shift/Win plus exactly one trailing key,
    /// joined by "+", case-insensitive. At least one modifier is required - a bare key parses to
    /// false so the caller can refuse it with Settings.HotkeyNeedsModifier instead of registering an
    /// accidental single-key global hotkey.
    /// </summary>
    public static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Key? parsedKey = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (parsedKey is not null || !Enum.TryParse(part, ignoreCase: true, out Key candidate) || !Enum.IsDefined(candidate))
                        return false;
                    parsedKey = candidate;
                    break;
            }
        }

        if (parsedKey is null || !HasRequiredModifier(modifiers, parsedKey.Value) || !IsUsableHotkeyKey(modifiers, parsedKey.Value))
            return false;

        key = parsedKey.Value;
        return true;
    }

    /// <summary>Not a key by itself (none, or a modifier key) and not one of the four window snap
    /// combinations (Ctrl+Alt+arrow) this app registers on its own.</summary>
    private static bool IsUsableHotkeyKey(ModifierKeys modifiers, Key key) =>
        key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        && !(modifiers == SnapModifiers && key is Key.Left or Key.Right or Key.Up or Key.Down);

    /// <summary>A global hotkey needs Control, Alt or Windows: with Shift alone it would swallow the
    /// plain capital letter in every program. Shift alone is fine with F1-F24, which type nothing.</summary>
    public static bool HasRequiredModifier(ModifierKeys modifiers, Key key) =>
        (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0
        || (modifiers == ModifierKeys.Shift && key is >= Key.F1 and <= Key.F24);

    /// <summary>The inverse of <see cref="TryParse"/> - the settings capture box turns a pressed
    /// combination back into this stored text form.</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
