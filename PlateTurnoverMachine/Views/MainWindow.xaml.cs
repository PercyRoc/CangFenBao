using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;

namespace PlateTurnoverMachine.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
internal partial class MainWindow
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

            // Use HandyControl MessageBox for confirmation
            var result = HandyControl.Controls.MessageBox.Show(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) 
            {
                e.Cancel = true;
                return;
            }

            // Dispose ViewModel if applicable
            if (DataContext is IDisposable viewModel)
            {
                // Run dispose in background
                Task.Run(() =>
                {
                    try
                    {
                        viewModel.Dispose();
                        Log.Information("主窗口ViewModel已释放");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放ViewModel时发生错误");
                    }
                });
            }

            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关闭程序时发生错误");
            e.Cancel = true;

            // Use HandyControl MessageBox for error
            HandyControl.Controls.MessageBox.Show(
                "关闭程序时发生错误，请重试",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}