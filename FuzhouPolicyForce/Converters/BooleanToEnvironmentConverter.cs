using System.Globalization;
using System.Windows.Data;

namespace FuzhouPolicyForce.Converters;

public class BooleanToEnvironmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isProduction)
        {
            return isProduction ? "当前环境：正式环境" : "当前环境：测试环境";
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}