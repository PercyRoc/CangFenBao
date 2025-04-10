using System.Windows;
using Common.Services.Ui;
using Serilog;
using SharedUI.Views.Settings;

namespace ZtCloudWarehous.Views;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);
        Loaded += OnUserControlLoaded;
    }
    private void OnUserControlLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation?.Navigate(typeof(CameraSettingsView)); 
        Log.Information("SettingsDialog UserControl 已加载，并导航到 CameraSettingsView");
    }
}