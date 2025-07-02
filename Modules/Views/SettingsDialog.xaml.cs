using System.Windows;
using Common.Services.Ui;
using Serilog;
using SharedUI.Views.Settings;

namespace ShanghaiModuleBelt.Views;

/// <summary>
///     SettingsDialog 的交互逻辑
/// </summary>
public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
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

        Log.Information("SettingsDialog已加载，导航到CameraSettingsView");
    }
}