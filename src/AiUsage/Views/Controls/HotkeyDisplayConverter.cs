using System.Globalization;
using System.Windows.Data;
using AiUsage.Services;

namespace AiUsage.Views.Controls;

/// <summary>The stored hotkey text to the form shown to the user. The second value is any
/// localized string, bound only so a language switch reformats the modifier names.</summary>
public sealed class HotkeyDisplayConverter : IMultiValueConverter
{
    public static HotkeyDisplayConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        GlobalHotkey.FormatForDisplay(values.Length > 0 ? values[0] as string : null);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
