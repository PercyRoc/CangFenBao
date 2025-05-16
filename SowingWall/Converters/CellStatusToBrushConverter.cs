using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SowingWall.ViewModels; // Assuming SowingCellStatus is here

namespace SowingWall.Converters
{
    public class CellStatusToBrushConverter : IValueConverter
    {
        // Define brushes for different statuses (consider defining these as resources later)
        private readonly Brush _emptyBrush = Brushes.LightGray;
        private readonly Brush _pendingPutBrush = Brushes.DodgerBlue;
        private readonly Brush _completedBrush = Brushes.MediumSeaGreen;
        private readonly Brush _errorBrush = Brushes.OrangeRed;
        private readonly Brush _fullBrush = Brushes.DarkRed; // Or another distinct color

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SowingCellStatus status)
            {
                switch (status)
                {
                    case SowingCellStatus.Empty:
                        return _emptyBrush;
                    case SowingCellStatus.PendingPut:
                        return _pendingPutBrush;
                    case SowingCellStatus.Completed:
                        return _completedBrush;
                    case SowingCellStatus.Error:
                        return _errorBrush;
                    case SowingCellStatus.Full:
                        return _fullBrush;
                    default:
                        return Brushes.Transparent; // Fallback
                }
            }
            return Brushes.Transparent; // Fallback
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 