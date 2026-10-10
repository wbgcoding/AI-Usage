using System.Globalization;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>A percentage (0-100) and a track width to the horizontal offset of a 2 px tick on that
/// track, kept fully inside it at both ends.</summary>
public sealed class PercentOffsetConverter : IMultiValueConverter
{
    private const double TickWidth = 2;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = values.Length > 0 && values[0] is double p ? p : 0;
        var trackWidth = values.Length > 1 && values[1] is double w ? w : 0;
        if (trackWidth <= TickWidth)
            return 0d;
        return Math.Clamp(trackWidth * percent / 100.0 - TickWidth / 2, 0, trackWidth - TickWidth);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
