using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Presentation_XiYiGu.Views.Settings;
using Serilog;
using SharedUI.Views.Settings;

namespace Presentation_XiYiGu.Views;

/// <summary>
/// 设置窗口
/// </summary>
public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();

        notificationService.Register("SettingWindowGrowl", GrowlPanel);

        // Set service provider and navigate after window is loaded
        Loaded += OnLoaded;
        // Add title bar mouse event handler
        MouseDown += OnWindowMouseDown;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // Allow dragging the window when left button is pressed in the title bar area
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while dragging window");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Navigate to camera settings page
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
}