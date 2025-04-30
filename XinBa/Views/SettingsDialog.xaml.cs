using System.Windows;
using Common.Services.Ui;
using SharedUI.Views.Settings;


namespace XinBa.Views;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();
        notificationService.Register("SettingWindowGrowl", GrowlPanel);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
}