using System.Globalization;
using System.Windows.Data;

namespace SharedUI.Converters;

/// <summary>
///     基于转换器参数将枚举值转换为布尔值。
/// </summary>
public class EnumToBooleanConverter : IValueConverter
{
    /// <summary>
    ///     如果枚举值与参数匹配，则将其转换为true，否则为false。
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        // 确保参数为字符串，并且值可以表示为字符串
        var enumValueString = value.ToString();
        var parameterString = parameter.ToString();

        // 将枚举值的字符串表示形式与参数字符串进行比较
        return enumValueString != null && enumValueString.Equals(parameterString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     将true转换回参数指定的枚举值。
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Binding.DoNothing;

        // 仅当布尔值为true时才转换回 (选中RadioButton)
        if (value is not true) return Binding.DoNothing;
        var parameterString = parameter.ToString();
        try
        {
            // 尝试将参数字符串解析回目标枚举类型
            if (parameterString != null)
                return Enum.Parse(targetType, parameterString, true); //不区分大小写的解析
        }
        catch (ArgumentException) //生成参数与名称不匹配的框
        {
            return Binding.DoNothing;
        }

        // 如果value为false或不是布尔值，则不执行任何操作
        return Binding.DoNothing;
    }
}