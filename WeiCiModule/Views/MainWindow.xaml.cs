using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using MessageBox = HandyControl.Controls.MessageBox;
using System.Windows.Interop; // Required for WindowInteropHelper
using System.Windows.Forms;
using Application = System.Windows.Application; // Required for Screen class (needs reference to System.Windows.Forms.dll)

namespace WeiCiModule.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public MainWindow(INotificationService notificationService)
    {
        InitializeComponent();

        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);

        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 当在标题栏区域按下左键时允许拖动窗口
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖动窗口时发生错误");
        }
    }

    private void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = MessageBox.Show(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关闭程序时发生错误");
            e.Cancel = true;
            MessageBox.Show("关闭程序时发生错误，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var windowInteropHelper = new WindowInteropHelper(this);
            IntPtr hwnd = windowInteropHelper.Handle; 

            if (hwnd == IntPtr.Zero)
            {
                Log.Warning("MainWindow_Loaded: 窗口句柄为零，无法确定屏幕。跳过调整大小。");
                return;
            }

            Screen currentScreen = Screen.FromHandle(hwnd);

            // 重置窗口状态以应用边界更改
            this.WindowState = WindowState.Normal; 

            this.Left = currentScreen.WorkingArea.Left;
            this.Top = currentScreen.WorkingArea.Top;
            this.Width = currentScreen.WorkingArea.Width;
            this.Height = currentScreen.WorkingArea.Height;

            Log.Information("MainWindow_Loaded: 窗口已调整大小以适应屏幕 '{ScreenDeviceName}' 的工作区: {Width}x{Height} @ ({Left},{Top})",
                            currentScreen.DeviceName, this.Width, this.Height, this.Left, this.Top);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在 MainWindow_Loaded 中尝试将窗口调整到全屏时发生错误。");
            // 回退或不执行任何操作，窗口将使用其默认XAML大小
        }
    }
}