using System.Globalization;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>How many figure cards share one row: all of them while the section is wide enough,
/// fewer (wrapping onto a second row) once it sits in a half-width column, so no card ever gets
/// narrower than its number and caption need. Values: the card count, then the available width.</summary>
public sealed class FigureCardColumnsConverter : IMultiValueConverter
{
    /// <summary>The narrowest card that still shows a full number and a two-line caption.</summary>
    internal const double MinCardWidth = 120;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var cards = values.Length > 0 && values[0] is int count && count > 0 ? count : 1;
        if (values.Length < 2 || values[1] is not double width || width <= 0 || double.IsNaN(width))
            return cards;

        return Math.Clamp((int)(width / MinWidth(parameter)), 1, cards);
    }

    /// <summary>The narrowest card for this use: the converter parameter when it is a positive number
    /// (the forecast cards need more room than a figure), else <see cref="MinCardWidth"/>.</summary>
    private static double MinWidth(object? parameter) =>
        parameter is not null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && min > 0
            ? min : MinCardWidth;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
