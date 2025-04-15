using Common.Services.Ui;

namespace Sunnen.Views.Dialogs;

public partial class HistoryControl
{
    public HistoryControl(INotificationService notificationService)
    {
        InitializeComponent();
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
}