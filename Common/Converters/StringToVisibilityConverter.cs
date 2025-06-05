using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Common.Converters;

/// <summary>
/// 字符串到可见性的转换器
/// 当字符串为null或空时返回Collapsed，否则返回Visible
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public class StringToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 当字符串有值时显示的可见性，默认为Visible
    /// </summary>
    public Visibility TrueValue { get; set; } = Visibility.Visible;
    
    /// <summary>
    /// 当字符串为空或null时显示的可见性，默认为Collapsed
    /// </summary>
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string stringValue)
        {
            return string.IsNullOrWhiteSpace(stringValue) ? FalseValue : TrueValue;
        }
        
        return value == null ? FalseValue : TrueValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException("StringToVisibilityConverter does not support ConvertBack");
    }
} 