using System;
using System.Globalization;
using System.Windows.Data;
using WPFLocalizeExtension.Extensions;

namespace Weight.Converters;

public class EnumToLocalizedStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return null;

        // 键的格式将是 "[资源字典前缀]:[资源文件名]:[parameter]_[value]"
        // 例如 "Weight:Resources.WeightSettings:WeightType_Static"
        // 或者如果DefaultAssembly和DefaultDictionary已在XAML中指定，则可以简化
        string resourceKey = $"{parameter}_{value}";
        
        // 尝试从WPFLocalizeExtension获取本地化字符串
        // 第一个参数是完整的资源键，包括程序集和字典名称
        // 第二个参数是当前文化信息
        // string localizedValue = LocExtension.GetLocalizedValue<string>($"Weight:Resources.WeightSettings:{resourceKey}", culture);
        
        // 由于 DefaultAssembly="Weight" 和 DefaultDictionary="Resources.WeightSettings" 已在 XAML 中定义，
        // 我们可以直接使用资源键。
        string localizedValue = LocExtension.GetLocalizedValue<string>(resourceKey, culture);

        // 如果找不到本地化字符串，则返回枚举值的原始字符串表示
        return string.IsNullOrEmpty(localizedValue) ? value.ToString() : localizedValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 通常不需要双向绑定枚举的本地化字符串，因此这里可以不实现
        throw new NotImplementedException();
    }
} 