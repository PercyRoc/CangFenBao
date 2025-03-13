using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Presentation_XiYiGu.Converters;

/// <summary>
/// 字符串到可见性的转换器
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 转换值
    /// </summary>
    /// <param name="value">要转换的值</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">区域性信息</param>
    /// <returns>转换后的值</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 转换回值
    /// </summary>
    /// <param name="value">要转换的值</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">区域性信息</param>
    /// <returns>转换后的值</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 反向字符串到可见性的转换器
/// </summary>
public class InverseStringToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 转换值
    /// </summary>
    /// <param name="value">要转换的值</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">区域性信息</param>
    /// <returns>转换后的值</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 转换回值
    /// </summary>
    /// <param name="value">要转换的值</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">区域性信息</param>
    /// <returns>转换后的值</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
} 