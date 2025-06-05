using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Common.Converters;

/// <summary>
/// 空对象到可见性的转换器
/// 当对象为null时返回Collapsed，否则返回Visible
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public class NullToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 当对象不为null时显示的可见性，默认为Visible
    /// </summary>
    public Visibility NotNullValue { get; set; } = Visibility.Visible;
    
    /// <summary>
    /// 当对象为null时显示的可见性，默认为Collapsed
    /// </summary>
    public Visibility NullValue { get; set; } = Visibility.Collapsed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value == null ? NullValue : NotNullValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException("NullToVisibilityConverter does not support ConvertBack");
    }
} 