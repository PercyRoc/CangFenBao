using System.Windows;
using System.Windows.Controls;
using Camera.Views;
using Common.Services.Ui;

namespace Rookie.Views.Dialogs;

public partial class SettingsDialogs : UserControl
{
    public SettingsDialogs(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);

        // 在控件加载完成后导航
        Loaded += OnLoaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 导航到相机设置页面
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
}