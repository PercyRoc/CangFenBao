using System;
using System.Globalization;
using System.Windows.Data;
using MahApps.Metro.IconPacks; // Make sure this package is referenced
using SowingWall.ViewModels; // Assuming SowingCellStatus is here

namespace SowingWall.Converters
{
    public class CellStatusToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SowingCellStatus status)
            {
                switch (status)
                {
                    case SowingCellStatus.Empty:
                        return PackIconMaterialKind.CheckboxBlankOutline;
                    case SowingCellStatus.PendingPut:
                        return PackIconMaterialKind.ArrowDownBoldBoxOutline; // Icon for needing input
                    case SowingCellStatus.Completed:
                        return PackIconMaterialKind.CheckboxMarkedCircleOutline;
                    case SowingCellStatus.Error:
                        return PackIconMaterialKind.AlertCircleOutline;
                    case SowingCellStatus.Full:
                        return PackIconMaterialKind.ArchiveArrowDownOutline; // Icon for full/archived
                    default:
                        return PackIconMaterialKind.HelpCircleOutline; // Fallback
                }
            }
            return PackIconMaterialKind.HelpCircleOutline; // Fallback
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 