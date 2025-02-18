using System.ComponentModel;
using System.Windows;
using Presentation_CommonLibrary.Services;
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