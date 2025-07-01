using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ChileSowing.Converters;

/// <summary>
/// 将布尔值转换为状态颜色的转换器
/// True: 警告色 (处理中)
/// False: 成功色 (空闲)
/// </summary>
public class BooleanToStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isProcessing)
        {
            // 处理中显示警告色，空闲显示成功色
            return isProcessing 
                ? new SolidColorBrush(Color.FromRgb(255, 193, 7))   // 警告色 #FFC107
                : new SolidColorBrush(Color.FromRgb(76, 175, 80));  // 成功色 #4CAF50
        }
        
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException("BooleanToStatusColorConverter does not support ConvertBack");
    }
} 