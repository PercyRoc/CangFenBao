using System.Globalization;
using System.Windows.Data;

namespace XinBeiYang.Converters;

/// <summary>
///     将进度百分比(0-100)转换为角度(0-360)
/// </summary>
public class ProgressToAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double progress)
            // 将0-100的进度转换为0-360的角度
            return progress * 3.6;
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}