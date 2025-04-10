using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using KuaiLv.Services.DWS;
using Serilog;
using Prism.Ioc;
using MessageBox = HandyControl.Controls.MessageBox;

namespace KuaiLv.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private readonly IContainerProvider _containerProvider;
    private readonly OfflinePackageService _offlinePackageService;
    private bool _isClosing;

    public MainWindow(INotificationService notificationService, IContainerProvider containerProvider, OfflinePackageService offlinePackageService)
    {
        _containerProvider = containerProvider;
        _offlinePackageService = offlinePackageService;
        InitializeComponent();

        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);

        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
        Closing += MainWindow_Closing;
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

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var alreadyShuttingDown = Application.Current is App { _isShuttingDown: true };

        if (_isClosing || alreadyShuttingDown)
        {
            e.Cancel = _isClosing && !alreadyShuttingDown;
            return;
        }

        var result = MessageBox.Show(
            "确定要关闭应用程序吗？",
            "确认关闭",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            Log.Information("用户取消了关闭操作");
            return;
        }

        // 用户确认关闭，但在执行前检查离线包裹
        try
        {
            Log.Information("正在检查离线包裹...");
            var offlinePackages = await _offlinePackageService.GetOfflinePackagesAsync();

            if (offlinePackages.Count != 0)
            {
                Log.Warning("检测到 {Count} 个离线包裹，阻止关闭。", offlinePackages.Count);
                MessageBox.Show(
                    "检测到未上传的离线包裹，请检查网络连接并稍后重试关闭。",
                    "无法关闭",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                e.Cancel = true; // 阻止关闭
                return;
            }
            Log.Information("未检测到离线包裹，继续关闭流程。");
        }
        catch (Exception ex)
        {
             Log.Error(ex, "检查离线包裹时发生错误，允许继续关闭。");
        }

        // 没有离线包裹，或检查出错，继续执行关闭流程
        _isClosing = true; // 标记正在关闭
        Log.Information("用户确认关闭，开始关闭应用程序...");

        // 必须设置 Cancel = true 以允许异步关闭
        e.Cancel = true;

        try
        {
            if (Application.Current is App app)
            {
                await app.ShutdownServicesAsync(_containerProvider);
            }
            else
            {
                Log.Error("无法获取应用程序实例以关闭服务");
            }

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关闭应用程序时发生错误");
            Application.Current.Shutdown();
        }
    }
}