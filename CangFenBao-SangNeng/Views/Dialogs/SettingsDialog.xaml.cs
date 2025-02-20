using Presentation_CommonLibrary.Services;
using Wpf.Ui.Controls;
using CangFenBao_SangNeng.Views.Settings;
using System.Windows;

namespace CangFenBao_SangNeng.Views.Dialogs;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();
        
        notificationService.Register("SettingWindowGrowl", GrowlPanel);

        // 在窗口加载完成后设置服务提供程序并导航
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 导航到相机设置页面
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
} 