using System.Globalization;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>True when the bound width reaches the converter parameter (a number of device independent
/// pixels), so a layout can switch between a wide and a narrow arrangement. An unknown width counts as wide.</summary>
public sealed class AtLeastWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || width <= 0 || double.IsNaN(width))
            return true;
        return parameter is not null
            && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum)
            && width >= minimum;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
