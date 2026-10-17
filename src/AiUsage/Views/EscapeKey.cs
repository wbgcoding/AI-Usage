using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsage.Views;

/// <summary>Decides whether Escape should close a window. A window-level tunnelling handler sees the
/// key before an open drop-down list or popup does, so Escape meant to dismiss one of those must be
/// left alone.</summary>
internal static class EscapeKey
{
    /// <summary>True for Escape that no open combo box dropdown or popup is about to use.</summary>
    public static bool ClosesWindow(KeyEventArgs e) =>
        e.Key == Key.Escape
        && !InOpenComboBox(e.OriginalSource as DependencyObject)
        && !InOpenComboBox(Keyboard.FocusedElement as DependencyObject)
        && OpenPopupOf(e.OriginalSource as DependencyObject) is null
        && OpenPopupOf(Keyboard.FocusedElement as DependencyObject) is null;

    /// <summary>The innermost open popup <paramref name="node"/> sits in (a date picker's calendar, a
    /// window's own panel), or null outside any.</summary>
    public static Popup? OpenPopupOf(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Popup { IsOpen: true } self)
                return self;

            // A popup's content hangs off its own root visual; only the logical tree leads back to it.
            var logicalParent = LogicalTreeHelper.GetParent(node);
            if (logicalParent is Popup { IsOpen: true } owner)
                return owner;

            node = (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null)
                ?? logicalParent;
        }

        return null;
    }

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
