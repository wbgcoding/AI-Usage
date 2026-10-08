using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsage.Views;

/// <summary>Decides whether Escape should close a window. A window-level tunnelling handler sees the
/// key before an open drop-down list does, so Escape meant to dismiss the list must be left alone.</summary>
internal static class EscapeKey
{
    /// <summary>True for Escape that no open combo box dropdown is about to use.</summary>
    public static bool ClosesWindow(KeyEventArgs e) =>
        e.Key == Key.Escape
        && !InOpenComboBox(e.OriginalSource as DependencyObject)
        && !InOpenComboBox(Keyboard.FocusedElement as DependencyObject);

    /// <summary>True when <paramref name="node"/> is a combo box with its list open or sits inside one.</summary>
    public static bool InOpenComboBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ComboBox { IsDropDownOpen: true })
                return true;

            if (node is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true })
                return true;

            node = (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null)
                ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
