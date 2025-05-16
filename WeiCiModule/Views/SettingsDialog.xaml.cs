using System.Windows.Controls;
using Common.Services.Ui;

namespace WeiCiModule.Views;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);
    }
}