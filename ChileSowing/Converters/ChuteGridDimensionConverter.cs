using System;
using System.Globalization;
using System.Windows.Data;

namespace ChileSowing.Converters
{
    /// <summary>
    /// 格口网格尺寸转换器，根据格口数量计算合适的行列数
    /// </summary>
    public class ChuteGridDimensionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int chuteCount || chuteCount <= 0)
                return 1;

            // 根据格口数量计算合适的列数
            // 目标是让格口尽量呈现近似正方形的排列
            var columns = (int)Math.Ceiling(Math.Sqrt(chuteCount));
            
            // 对于常见的格口数量，使用优化的布局
            return chuteCount switch
            {
                <= 12 => Math.Min(6, columns),    // 小于等于12个，最多6列
                <= 30 => Math.Min(8, columns),    // 小于等于30个，最多8列
                <= 60 => Math.Min(10, columns),   // 小于等于60个，最多10列
                <= 100 => Math.Min(12, columns),  // 小于等于100个，最多12列
                _ => Math.Min(15, columns)         // 超过100个，最多15列
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 格口网格行数转换器
    /// </summary>
    public class ChuteGridRowsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int chuteCount || chuteCount <= 0)
                return 1;

            // 使用相同的列数计算逻辑
            var columns = chuteCount switch
            {
                <= 12 => Math.Min(6, (int)Math.Ceiling(Math.Sqrt(chuteCount))),
                <= 30 => Math.Min(8, (int)Math.Ceiling(Math.Sqrt(chuteCount))),
                <= 60 => Math.Min(10, (int)Math.Ceiling(Math.Sqrt(chuteCount))),
                <= 100 => Math.Min(12, (int)Math.Ceiling(Math.Sqrt(chuteCount))),
                _ => Math.Min(15, (int)Math.Ceiling(Math.Sqrt(chuteCount)))
            };

            // 计算需要的行数
            return (int)Math.Ceiling((double)chuteCount / columns);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 