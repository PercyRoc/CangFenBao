using System.Windows;
using Common.Services.Ui;
using Serilog;
using Wpf.Ui.Controls;

namespace XinBeiYang.Views;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);
    }

    private void SettingsDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 确保 RootNavigation 存在并且已经加载
        if (RootNavigation is not { } navigationView) return;

        // 找到 Tag 为 "Camera" 的导航项
        var cameraItem = navigationView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag is "Camera");

        if (cameraItem != null)
        {
            // 导航到相机设置页面
            if (cameraItem.TargetPageType != null) navigationView.Navigate(cameraItem.TargetPageType);

            // 如果需要，也可以尝试直接设置选中项（但Navigate通常是更可靠的方式）
            // navigationView.SetCurrentValue(NavigationView.SelectedItemProperty, cameraItem);
        }
        else
        {
            Log.Warning("未能在 NavigationView 中找到 Tag 为 'Camera' 的导航项。");
        }
    }
}