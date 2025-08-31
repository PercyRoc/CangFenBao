using System.Globalization;
using System.Windows.Data;

namespace SharedUI.Converters;

/// <summary>
///     布尔值到字符串转换器
/// </summary>
public class BooleanToStringConverter : IValueConverter
{
    /// <summary>
    ///     True对应的值
    /// </summary>
    public string TrueValue { get; set; } = "True";

    /// <summary>
    ///     False对应的值
    /// </summary>
    public string FalseValue { get; set; } = "False";

    /// <summary>
    ///     转换布尔值到字符串
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue) return boolValue ? TrueValue : FalseValue;

        return FalseValue;
    }

    /// <summary>
    ///     转换字符串到布尔值
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string stringValue) return false;
        if (stringValue == TrueValue) return true;
        return stringValue == FalseValue && false;
    }
}