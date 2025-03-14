using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Presentation_BenFly.ViewModels.Windows;
using Serilog;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Presentation_BenFly.Views.Windows;

/// <summary>
///     MainWindow.xaml 的交互逻辑
/// </summary>
public partial class MainWindow
{
    private readonly IDialogService _dialogService;

    public MainWindow(IDialogService dialogService, INotificationService notificationService)
    {
        _dialogService = dialogService;
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

    private async void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = await _dialogService.ShowIconConfirmAsync(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // 释放MainWindowViewModel
            if (DataContext is MainWindowViewModel viewModel)
                // 在后台线程中执行Dispose操作，避免UI线程阻塞
                await Task.Run(() =>
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

            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关闭程序时发生错误");
            e.Cancel = true;
            await _dialogService.ShowErrorAsync("关闭程序时发生错误，请重试", "错误");
        }
    }
}