using System.Threading;
using System.Windows.Controls;
using AiUsage.Views;

namespace AiUsage.Tests;

public class EscapeKeyTests
{
    // WPF objects are thread-affine, so each case builds and reads them on one STA thread.
    private static T OnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = work();
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
            throw new InvalidOperationException("STA work failed.", failure);
        return result!;
    }

    [Fact]
    public void An_open_combo_box_and_anything_inside_it_keeps_Escape_for_the_list() =>
        Assert.Equal((true, true, false), OnSta(() =>
        {
            // The drop-down only opens once the combo box is loaded, so it sits in a real window off screen.
            var inner = new TextBlock();
            var combo = new ComboBox { IsDropDownOpen = true };
            combo.Items.Add(inner);
            var closed = new ComboBox();
            var innerClosed = new TextBlock();
            closed.Items.Add(innerClosed);
            var window = new System.Windows.Window
            {
                Left = -10000,
                Top = -10000,
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Content = new StackPanel { Children = { combo, closed } },
            };
            try
            {
                window.Show();
                combo.IsDropDownOpen = true;
                return (EscapeKey.InOpenComboBox(combo), EscapeKey.InOpenComboBox(inner), EscapeKey.InOpenComboBox(innerClosed));
            }
            finally
            {
                window.Close();
            }
        }));

    [Fact]
    public void Elements_outside_any_combo_box_do_not_block_Escape() =>
        Assert.False(OnSta(() => EscapeKey.InOpenComboBox(new StackPanel { Children = { new Button() } }.Children[0])));
}
