using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Presentation_KuaiLv.Converters;

public class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string colorString) return Brushes.Black;
        
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorString);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Brushes.Black;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}