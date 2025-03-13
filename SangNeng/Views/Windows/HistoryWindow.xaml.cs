using Common.Services.Ui;

namespace Presentation_SangNeng.Views.Windows;

public partial class HistoryWindow
{
    public HistoryWindow(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
}