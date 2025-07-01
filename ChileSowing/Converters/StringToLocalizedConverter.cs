using System.Globalization;
using System.Windows.Data;
using WPFLocalizeExtension.Engine;

namespace ChileSowing.Converters;

/// <summary>
/// 字符串到本地化文本转换器
/// 当输入为空或null时，返回本地化的"无"
/// </summary>
public class StringToLocalizedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str && !string.IsNullOrEmpty(str))
        {
            return str;
        }
        
        // 返回本地化的"无"
        return LocalizeDictionary.Instance.GetLocalizedObject("ChileSowing:Resources/Strings:Status_None", null, culture) ?? "None";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException("StringToLocalizedConverter does not support ConvertBack");
    }
} 