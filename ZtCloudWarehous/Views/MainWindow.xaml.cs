using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using ZtCloudWarehous.ViewModels;

namespace ZtCloudWarehous.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
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
        // Loaded 事件已在 XAML 中关联
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 根据当前屏幕的工作区自动计算并设置窗口位置和大小
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;
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
            e.Cancel = true; // 先阻止关闭

            // 使用 HandyControl 的 MessageBox 显示确认对话框
            var result = HandyControl.Controls.MessageBox.Show(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true; // 用户取消关闭
                return;
            }

            // 释放MainWindowViewModel (保持异步释放)
            if (DataContext is MainWindowViewModel viewModel)
            {
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
            }

            e.Cancel = false; // 允许关闭
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关闭程序时发生错误");
            e.Cancel = true; // 发生错误，阻止关闭

            // 使用 HandyControl 的 MessageBox 显示错误对话框
            HandyControl.Controls.MessageBox.Show(
                "关闭程序时发生错误，请重试",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}