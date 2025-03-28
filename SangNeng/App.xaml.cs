using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Weight;
using DeviceService.Extensions;
using HandyControl.Controls;
using Presentation_SangNeng.ViewModels.Settings;
using Presentation_SangNeng.ViewModels.Windows;
using Presentation_SangNeng.Views.Windows;
using Prism.Ioc;
using SangNeng.Services;
using SangNeng.ViewModels.Dialogs;
using SangNeng.ViewModels.Settings;
using SangNeng.ViewModels.Windows;
using SangNeng.Views.Dialogs;
using SangNeng.Views.Settings;
using Serilog;
using SharedUI.Extensions;
using Window = System.Windows.Window;

namespace SangNeng;

/// <summary>
///     应用程序入口
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "SangNeng_App_Mutex";
    private CircleProgressBar? _loadingControl;
    private Window? _loadingWindow;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew) return Container.Resolve<MainWindow>();

        // 关闭当前实例
        Current.Shutdown();
        return null!;
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册通用服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();

        // 注册设备服务
        containerRegistry.AddPhotoCamera() // 拍照相机
            .AddVolumeCamera() // 体积相机
            .AddScanner() // 扫码枪
            .AddWeightScale(); // 重量称

        // 注册桑能服务
        containerRegistry.RegisterSingleton<ISangNengService, SangNengService>();

        // 注册窗口和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
        containerRegistry.Register<Window, HistoryWindow>("HistoryWindow");
        containerRegistry.Register<HistoryWindowViewModel>();

        // 注册设置页面
        containerRegistry.Register<VolumeSettingsView>();
        containerRegistry.Register<WeightSettingsView>();
        containerRegistry.Register<PalletSettingsView>();

        // 注册设置页面的ViewModel
        containerRegistry.Register<VolumeSettingsViewModel>();
        containerRegistry.Register<WeightSettingsViewModel>();
        containerRegistry.Register<PalletSettingsViewModel>();

        // 注册桑能设置页面
        containerRegistry.RegisterForNavigation<SangNengSettingsPage, SangNengSettingsViewModel>();
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30)
            .CreateLogger();

        Log.Information("应用程序启动");

        // 在主线程创建和显示加载窗口
        _loadingWindow = new Window
        {
            Title = "Starting...",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Width = 300,
            Height = 150,
            Topmost = true
        };

        var grid = new Grid();
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0)
        });

        var stackPanel = new StackPanel
        {
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _loadingControl = new CircleProgressBar
        {
            Width = 60,
            Height = 60,
            Value = 0,
            IsIndeterminate = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Background = new SolidColorBrush(Color.FromArgb(50, 0, 122, 204))
        };

        var textBlock = new TextBlock
        {
            Text = "Initializing system...",
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51))
        };

        stackPanel.Children.Add(_loadingControl);
        stackPanel.Children.Add(textBlock);
        grid.Children.Add(stackPanel);
        _loadingWindow.Content = grid;
        _loadingWindow.Show();

        // 先调用基类方法初始化容器
        base.OnStartup(e);

        // 在后台线程启动服务
        Task.Run(async () =>
        {
            try
            {
                // 在Task.Run外层声明变量
                CameraStartupService cameraStartupService = null!;
                VolumeCameraStartupService volumeCameraStartupService = null!;
                ScannerStartupService scannerStartupService = null!;
                WeightStartupService weightStartupService = null!;

                await Current.Dispatcher.InvokeAsync(() =>
                {
                    // 赋值已声明的变量
                    cameraStartupService = Container.Resolve<CameraStartupService>();
                    volumeCameraStartupService = Container.Resolve<VolumeCameraStartupService>();
                    scannerStartupService = Container.Resolve<ScannerStartupService>();
                    weightStartupService = Container.Resolve<WeightStartupService>();
                });

                // 修复：分步启动服务并添加延迟
                UpdateProgress("Initializing camera service...", 20);
                await Task.Delay(100); // 给UI更新留出时间
                await cameraStartupService.StartAsync(CancellationToken.None);

                UpdateProgress("Initializing volume camera...", 40);
                await Task.Delay(100);
                await volumeCameraStartupService.StartAsync(CancellationToken.None);

                // 重点修复：扫码枪服务需要同步初始化
                UpdateProgress("Initializing scanner...", 60);
                try 
                {
                    await Current.Dispatcher.InvokeAsync(async () =>
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await scannerStartupService.StartAsync(cts.Token);
                        Log.Information("扫码枪服务初始化成功");
                    });
                }
                catch (OperationCanceledException)
                {
                    Log.Error("扫码枪服务初始化超时");
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "扫码枪服务初始化失败");
                    throw;
                }

                UpdateProgress("Initializing weight scale...", 80);
                await weightStartupService.StartAsync(CancellationToken.None);

                UpdateProgress("Initialization complete", 100);
                Log.Information("托管服务启动成功");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动托管服务时发生错误");
                throw;
            }
            finally
            {
                // 关闭加载窗口
                await Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_loadingWindow != null)
                    {
                        _loadingWindow.Close();
                        _loadingWindow = null;
                    }

                    _loadingControl = null;
                });
            }
        });
    }

    private void UpdateProgress(string message, double progress)
    {
        if (_loadingWindow == null || _loadingControl == null) return;

        Current.Dispatcher.Invoke(() =>
        {
            if (_loadingWindow.Content is not Grid grid ||
                grid.Children[1] is not StackPanel stackPanel) return;

            if (stackPanel.Children[1] is TextBlock textBlock)
            {
                textBlock.Text = message;
            }

            _loadingControl.Value = progress;
        });

        // 给UI一点时间更新
        Thread.Sleep(100);
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            var volumeCameraStartupService = Container.Resolve<VolumeCameraStartupService>();
            var scannerStartupService = Container.Resolve<ScannerStartupService>();
            var weightStartupService = Container.Resolve<WeightStartupService>();

            cameraStartupService.StopAsync(CancellationToken.None).Wait();
            volumeCameraStartupService.StopAsync(CancellationToken.None).Wait();
            scannerStartupService.StopAsync(CancellationToken.None).Wait();
            weightStartupService.StopAsync(CancellationToken.None).Wait();

            // 释放相机工厂
            if (Container.Resolve<CameraFactory>() is IDisposable cameraFactory)
            {
                cameraFactory.Dispose();
                Log.Information("相机工厂已释放");
            }

            // 释放相机服务
            if (Container.Resolve<ICameraService>() is IDisposable cameraService)
            {
                cameraService.Dispose();
                Log.Information("相机服务已释放");
            }

            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
        }
        finally
        {
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}