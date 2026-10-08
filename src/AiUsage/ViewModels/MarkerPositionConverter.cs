using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiUsage.ViewModels;

/// <summary>Positions <c>UsageBar.xaml</c>'s threshold marker inside the bar's track: a left
/// <see cref="Thickness"/> proportional to the threshold percent, clamped so the marker's own width
/// never pushes it past the track's right edge.</summary>
public sealed class MarkerPositionConverter : IMultiValueConverter
{
    /// <summary>Matches the marker Rectangle's own Width in UsageBar.xaml.</summary>
    private const double MarkerWidth = 1;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = values.Length > 0 && values[0] is double p ? (double?)p : null;
        var trackWidth = values.Length > 1 && values[1] is double w ? w : 0;

        if (percent is not { } value || trackWidth <= 0)
            return new Thickness(0);

        var left = Math.Clamp(trackWidth * value / 100.0, 0, Math.Max(0, trackWidth - MarkerWidth));
        return new Thickness(left, 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
