using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KuaiLv.Converters;

/// <summary>
/// Converts a boolean value to its inverse Visibility representation.
/// True becomes Collapsed, False becomes Visible.
/// </summary>
public class InvertBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        return boolValue ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
} 