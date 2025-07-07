using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Notifications;
using Serilog;

namespace LosAngelesExpress.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private bool _isShuttingDown;

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

    private void MetroWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown) return;

        try
        {
            _isShuttingDown = true;
            e.Cancel = true;
            var result = HandyControl.Controls.MessageBox.Show(
                "Are you sure you want to exit the application?",
                "Confirm Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                _isShuttingDown = false;
                e.Cancel = true;
                return;
            }

            // Dispose ViewModels in background
            if (DataContext is IDisposable viewModel)
                Task.Run(() =>
                {
                    try
                    {
                        viewModel.Dispose();
                        Log.Information("Main window ViewModels disposed.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error disposing ViewModels.");
                    }
                    finally
                    {
                        // Proceed with shutdown on the UI thread after disposal attempt
                        Dispatcher.Invoke(() =>
                        {
                            e.Cancel = false;
                            Application.Current.Shutdown();
                        });
                    }
                });
            else
            {
                // If no disposable ViewModels, shut down directly
                Log.Information("ViewModels not disposable or null, shutting down directly.");
                e.Cancel = false;
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during application shutdown process.");
            e.Cancel = true;
            _isShuttingDown = false;
            HandyControl.Controls.MessageBox.Show(
                "An error occurred while closing the application. Please try again.",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}