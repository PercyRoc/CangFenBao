using System.Windows;
using Wpf.Ui.Controls;
using AmericanQuickHands.ViewModels;
using AmericanQuickHands.Views.Settings;

namespace AmericanQuickHands.Views;

public partial class SettingsDialog
{
    public SettingsDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }
    
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口加载完成后自动导航到美国快手API设置页面
        RootNavigation?.Navigate(typeof(AmericanQuickHandsApiSettingsView));
    }
}