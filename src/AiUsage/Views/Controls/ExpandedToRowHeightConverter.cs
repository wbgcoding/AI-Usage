using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>A grid row's height from a section's expansion: an expanded section's row takes an equal
/// share of the room the row group is given (a star row), a collapsed one only the height of its own
/// header (an auto row) - so a column of sections can fill exactly the height of the taller column
/// beside it without a collapsed section leaving an empty band. A parameter of true marks a section
/// that sizes to its content: its row stays an auto row even when expanded.</summary>
public sealed class ExpandedToRowHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not true ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
