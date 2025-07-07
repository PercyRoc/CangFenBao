using System.Windows;
using Common.Services.Notifications;
using Serilog;
using SowingSorting.Views.Settings;

namespace ChileSowing.Views;

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
            // 默认导航到TCP Modbus Settings页面
            RootNavigation?.Navigate(typeof(ModbusTcpSettingsView));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导航到设置页面时发生错误");
        }
    }
}