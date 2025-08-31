using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using XinBa.ViewModels;

namespace XinBa.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public MainWindow(INotificationService notificationService)
    {
        InitializeComponent();

        // Register Growl container
        notificationService.Register("MainWindowGrowl", GrowlPanel);

        // Add title bar mouse event handler
        MouseDown += OnWindowMouseDown;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while dragging window");
        }
    }

    private void ManualBarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        // 保留输入框但不处理条码输入功能
        // 可以在这里添加其他按键处理逻辑如果需要
    }

}