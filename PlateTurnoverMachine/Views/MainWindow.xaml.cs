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
        // 1. 阻止窗口立即关闭，显示确认对话框
        e.Cancel = true;

        // 2. 弹出确认对话框
        var result = HandyControl.Controls.MessageBox.Show(
            "确定要关闭程序吗？",
            "关闭确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            // 用户取消关闭，阻止窗口关闭
            e.Cancel = true;
            return;
        }

        // 3. 同步释放 ViewModel (如果需要)
        if (DataContext is IDisposable viewModel)
        {
            try
            {
                viewModel.Dispose();
                Log.Information("主窗口ViewModel已释放");
            }
            catch (Exception vmEx)
            {
                Log.Error(vmEx, "释放ViewModel时发生错误");
                // 仅记录错误，不阻止关闭
            }
        }

        // 4. 在后台执行清理工作，不等待完成
        try
        {
            Log.Information("开始执行应用程序关闭逻辑(后台)...");
            var app = (App)Application.Current;

            // 不等待资源清理完成，直接允许窗口关闭
            _ = Task.Run(async () =>
            {
                try
                {
                    await app.PerformShutdownAsync();
                    Log.Information("后台资源清理已完成，但可能在应用程序已退出后不会显示此日志");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "后台资源清理过程中发生错误");
                }
            });

            // 5. 立即允许窗口关闭
            e.Cancel = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动后台清理任务时发生错误");
            // 即使启动后台清理出错也允许关闭窗口
            e.Cancel = false;
        }
    }
}