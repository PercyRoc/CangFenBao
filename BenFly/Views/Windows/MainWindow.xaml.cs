using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BenFly.ViewModels.Windows;
using Common.Services.Ui;
using Serilog;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace BenFly.Views.Windows;

/// <summary>
///     MainWindow.xaml 的交互逻辑
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

    private void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            // 显示确认对话框
            var result = HandyControl.Controls.MessageBox.Show(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                // 用户点击"否"，取消关闭操作
                e.Cancel = true;
                Log.Information("用户取消了关闭操作");
                return;
            }

            Log.Information("用户确认关闭程序，开始执行关闭流程");
            // 用户确认关闭，让App.xaml.cs中的关闭处理程序接管
            // 不设置e.Cancel，让关闭事件继续传播到App.xaml.cs
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理关闭确认时发生错误");
            e.Cancel = true;
            HandyControl.Controls.MessageBox.Show(
                "处理关闭确认时发生错误，请重试",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OnBarcodeInput();
        }
    }
}