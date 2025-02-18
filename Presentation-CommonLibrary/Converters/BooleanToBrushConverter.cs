using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Presentation_CommonLibrary.Converters;

public class BooleanToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked)
            return isChecked
                ? Application.Current.Resources["NavigationIconBackgroundBrush"]
                : Application.Current.Resources["NavigationIconDefaultBrush"];
        return Application.Current.Resources["NavigationIconDefaultBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}