using Common.Services.Ui;

namespace SangNeng.Views.Windows;

public partial class HistoryControl
{
    public HistoryControl(INotificationService notificationService)
    {
        InitializeComponent();
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
}