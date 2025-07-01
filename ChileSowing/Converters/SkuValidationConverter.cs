using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace ChileSowing.Converters;

/// <summary>
/// SKU验证转换器，用于验证输入的SKU格式是否正确
/// </summary>
public class SkuValidationConverter : IValueConverter
{
    /// <summary>
    /// SKU的正则表达式模式，可以根据实际需求调整
    /// 当前模式：至少3个字符，可以包含字母、数字、短横线
    /// </summary>
    private const string SkuPattern = @"^[A-Za-z0-9\-]{3,}$";
    
    private static readonly Regex SkuRegex = new(SkuPattern, RegexOptions.Compiled);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string sku)
            return false;

        if (string.IsNullOrWhiteSpace(sku))
            return false;

        return SkuRegex.IsMatch(sku);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException("SkuValidationConverter does not support ConvertBack");
    }
} 