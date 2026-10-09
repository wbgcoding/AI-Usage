using System.Windows.Controls;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>The "Window level" submenu shared by the tray menu and the title bar menu: one parent item
/// with the three levels as radio-style entries, exactly one of them checked. Choosing an entry raises
/// <see cref="Chosen"/>; <see cref="Select"/> moves the check mark without raising anything, so a level
/// changed from another place never loops back as a fake click.</summary>
internal sealed class WindowLayerMenu
{
    private readonly Dictionary<string, MenuItem> _items = [];
    private bool _selecting;

    public WindowLayerMenu(string layer)
    {
        Header = new MenuItem();
        foreach (var value in WindowLayers.All)
        {
            var item = new MenuItem { IsCheckable = true, Tag = value };
            item.Click += (_, _) => OnClick(value);
            _items[value] = item;
            Header.Items.Add(item);
        }

        RefreshText();
        Select(layer);
    }

    /// <summary>The parent entry to add to a menu.</summary>
    public MenuItem Header { get; }

    /// <summary>Raised with the chosen level when the user picks an entry.</summary>
    public event EventHandler<string>? Chosen;

    /// <summary>Checks the entry for <paramref name="layer"/> and unchecks the others.</summary>
    public void Select(string layer)
    {
        var normalized = WindowLayers.Normalize(layer);
        _selecting = true;
        foreach (var (value, item) in _items)
            item.IsChecked = value == normalized;
        _selecting = false;
    }

    /// <summary>Re-reads every text in the active language.</summary>
    public void RefreshText()
    {
        var loc = LocalizationService.Instance;
        Header.Header = loc["Settings.WindowLayer"];
        foreach (var (value, item) in _items)
            item.Header = loc["WindowLayer." + value];
    }

    private void OnClick(string value)
    {
        if (_selecting)
            return;

        // A radio entry never unchecks itself: clicking the checked one keeps it checked.
        Select(value);
        Chosen?.Invoke(this, value);
    }
}
