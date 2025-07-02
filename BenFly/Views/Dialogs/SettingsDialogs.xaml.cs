using System.Windows;
using Common.Services.Ui;
using SharedUI.Views.Settings;

namespace BenFly.Views.Dialogs;

public partial class SettingsDialogs
{
    public SettingsDialogs(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);

        // 在控件加载完成后设置服务提供程序并导航
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 导航到相机设置页面
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
}