using System.Windows;
using System.Windows.Controls;
using LosAngelesExpress.Views.Settings;
using Common.Services.Notifications;

namespace LosAngelesExpress.Views;

public partial class SettingsDialog
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
        // 导航到菜鸟设置页面
        RootNavigation?.Navigate(typeof(CainiaoSettingsView));
    }
}