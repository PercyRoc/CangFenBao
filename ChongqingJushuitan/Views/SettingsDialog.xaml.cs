using System.Windows;
using Common.Services.Ui;
using Serilog;
using SharedUI.Views.Settings;

namespace ChongqingYekelai.Views;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);

        // 在控件加载完成后导航到第一个页面
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导航到相机设置页面
            RootNavigation?.Navigate(typeof(CameraSettingsView));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导航到设置页面时发生错误");
        }
    }
}