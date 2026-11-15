using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AiUsage.ViewModels;

/// <summary>Positions a marker inside <c>UsageBar.xaml</c>'s track (the threshold line, the pace
/// tick): a left <see cref="Thickness"/> proportional to the percent, clamped so the marker's own
/// width never pushes it past the track's right edge. Without a converter parameter the marker is 1 px
/// wide and starts at the percent; with one (the marker's width in px) it is centred on the percent.</summary>
public sealed class MarkerPositionConverter : IMultiValueConverter
{
    /// <summary>Width of the threshold marker Rectangle in UsageBar.xaml, used when no parameter is given.</summary>
    private const double DefaultMarkerWidth = 1;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = values.Length > 0 && values[0] is double p ? (double?)p : null;
        var trackWidth = values.Length > 1 && values[1] is double w ? w : 0;

        if (percent is not { } value || trackWidth <= 0)
            return new Thickness(0);

        var centred = parameter is not null;
        var markerWidth = DefaultMarkerWidth;
        if (parameter is not null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            markerWidth = parsed;
        var start = trackWidth * value / 100.0 - (centred ? markerWidth / 2 : 0);
        var left = Math.Clamp(start, 0, Math.Max(0, trackWidth - markerWidth));
        return new Thickness(left, 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
