using System.Windows;
using System.Windows.Controls;
using Common.Services.Ui;
using Serilog;
using SharedUI.Views.Settings;

namespace FuzhouPolicyForce.Views;

public partial class SettingsDialog : UserControl
{
    public SettingsDialog(INotificationService notificationService)
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