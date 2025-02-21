using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Presentation_CommonLibrary.Services;
using Serilog;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Presentation_SangNeng.Views.Windows;

/// <summary>
/// Interaction logic for MainWindow.xaml
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

    private async void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = await _dialogService.ShowIconConfirmAsync(
                "Are you sure you want to close the program?",
                "Close Confirmation",
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while closing the program");
            e.Cancel = true;
            await _dialogService.ShowErrorAsync("Error occurred while closing the program, please try again", "Error");
        }
    }
    
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 当在标题栏区域按下左键时允许拖动窗口
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32)
            {
                DragMove();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖动窗口时发生错误");
        }
    }
}