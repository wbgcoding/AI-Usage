using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>Hides the Mini row's clock text once the tile is too narrow to show it next to the
/// percent value - a DataTrigger only compares for equality and cannot express "at or above a
/// width", so the threshold lives here as a named constant instead of a bare number in XAML.</summary>
public sealed class TileWidthToVisibilityConverter : IValueConverter
{
    /// <summary>Below this tile width the clock segment collapses; the percent value always stays
    /// and keeps its place.</summary>
    private const double MinWidthForClock = 300;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width >= MinWidthForClock ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
