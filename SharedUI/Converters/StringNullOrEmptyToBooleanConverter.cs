using System.Globalization;
using System.Windows.Data;

namespace SharedUI.Converters;

public class StringNullOrEmptyToBooleanConverter : IValueConverter
{
    public bool IsInverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var result = !string.IsNullOrEmpty(value as string);
        return IsInverted ? result : !result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}