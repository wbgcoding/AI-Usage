using System.Globalization;
using System.Windows.Data;
using AiUsage.Services;

namespace AiUsage.Views.Controls;

/// <summary>A label followed by its value in the order and spacing of the display language (ja and
/// zh-Hans set no space between them); the label alone while the value is empty. The values are the
/// label and the value text.</summary>
public sealed class LabelAndValueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = values.Length > 0 ? values[0] as string ?? "" : "";
        var value = values.Length > 1 ? values[1] as string ?? "" : "";
        return value.Length == 0 ? label : TextCompose.Sentences(label, value);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
