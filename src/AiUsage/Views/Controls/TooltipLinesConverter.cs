using System.Globalization;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>The non-empty values as tooltip lines, one per line; null (no tooltip) when every value is
/// empty. Lets a card keep its own explanation and add a line only when it applies, such as the note
/// on what a star beside a cost means.</summary>
public sealed class TooltipLinesConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var lines = values.OfType<string>().Where(value => value.Length > 0).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
