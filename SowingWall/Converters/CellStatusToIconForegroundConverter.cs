using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SowingWall.ViewModels; // Assuming SowingCellStatus is here

namespace SowingWall.Converters
{
    public class CellStatusToIconForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Typically, icons on colored backgrounds look good in White or a very light gray.
            // You might want more complex logic if backgrounds vary significantly.
            return Brushes.WhiteSmoke; 
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 