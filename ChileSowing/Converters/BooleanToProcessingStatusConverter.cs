using System.Globalization;
using System.Windows.Data;
using WPFLocalizeExtension.Engine;

namespace ChileSowing.Converters;

/// <summary>
/// 将布尔值转换为处理状态文本的转换器
/// True: 本地化的"处理中" 
/// False: 本地化的"空闲"
/// </summary>
public class BooleanToProcessingStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isProcessing)
        {
            string locKey = isProcessing ? "Status_Processing" : "Status_Idle";
            return LocalizeDictionary.Instance.GetLocalizedObject("ChileSowing:Resources/Strings:" + locKey, null, culture) ?? locKey;
        }
        
        return LocalizeDictionary.Instance.GetLocalizedObject("ChileSowing:Resources/Strings:Status_None", null, culture) ?? "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException("BooleanToProcessingStatusConverter does not support ConvertBack");
    }
} 