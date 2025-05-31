using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Common.Converters;

/// <summary>
///     枚举值到可见性的转换器
/// </summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Visibility.Collapsed;

        return value.Equals(parameter) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}