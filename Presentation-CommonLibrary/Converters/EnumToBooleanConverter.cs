using System.Globalization;
using System.Windows.Data;

namespace Presentation_CommonLibrary.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        var currentValue = value.ToString();
        var targetValue = parameter.ToString();
        return currentValue?.Equals(targetValue, StringComparison.InvariantCultureIgnoreCase) ?? false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null || value is not bool)
            return null;

        var useValue = (bool)value;
        if (useValue)
            return parameter;

        var enumValues = Enum.GetValues(targetType);
        foreach (var enumValue in enumValues)
            if (!enumValue.ToString()!.Equals(parameter.ToString(), StringComparison.InvariantCultureIgnoreCase))
                return enumValue;

        return Enum.GetValues(targetType).GetValue(0);
    }
}