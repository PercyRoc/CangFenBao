using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace Common.Converters;

public class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return string.Empty;

        var enumValue = (Enum)value;
        var field = enumValue.GetType().GetField(enumValue.ToString());
        if (field == null) return string.Empty;

        var attributes = (DescriptionAttribute[])field.GetCustomAttributes(typeof(DescriptionAttribute), false);
        return attributes.Length > 0 ? attributes[0].Description : enumValue.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}