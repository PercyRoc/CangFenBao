using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Common.Converters
{
    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "成功" => new SolidColorBrush(Color.FromRgb(39, 174, 96)),     // 成功绿色
                    "异常" => new SolidColorBrush(Color.FromRgb(231, 76, 60)),     // 异常红色
                    "警告" => new SolidColorBrush(Color.FromRgb(243, 156, 18)),    // 警告橙色
                    "已分拣" => new SolidColorBrush(Color.FromRgb(39, 174, 96)),   // 已分拣绿色
                    "地址无法识别" => new SolidColorBrush(Color.FromRgb(231, 76, 60)), // 无法识别红色
                    _ => new SolidColorBrush(Color.FromRgb(160, 160, 160))         // 默认灰色
                };
            }
            
            return new SolidColorBrush(Color.FromRgb(160, 160, 160));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class StatusToTextColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "成功" => new SolidColorBrush(Colors.White),
                    "异常" => new SolidColorBrush(Colors.White),
                    "警告" => new SolidColorBrush(Colors.White),
                    "已分拣" => new SolidColorBrush(Colors.White),
                    "地址无法识别" => new SolidColorBrush(Colors.White),
                    _ => new SolidColorBrush(Colors.White)
                };
            }
            
            return new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class RecognitionStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status.Contains("成功"))
                    return new SolidColorBrush(Color.FromRgb(39, 174, 96));
                if (status.Contains("异常") || status.Contains("无法识别"))
                    return new SolidColorBrush(Color.FromRgb(231, 76, 60));
                if (status.Contains("警告"))
                    return new SolidColorBrush(Color.FromRgb(243, 156, 18));
            }
            
            return new SolidColorBrush(Color.FromRgb(224, 224, 224));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 