using Presentation_CommonLibrary.Services;

namespace Presentation_SangNeng.Views.Windows;

public partial class HistoryWindow
{
    public HistoryWindow(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
}