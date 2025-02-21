using System.Windows;
using Presentation_SangNeng.ViewModels.Dialogs;
using Presentation_SangNeng.ViewModels.Settings;
using Presentation_SangNeng.ViewModels.Windows;
using Presentation_SangNeng.Views.Dialogs;
using Presentation_SangNeng.Views.Settings;
using Presentation_SangNeng.Views.Windows;
using CommonLibrary.Extensions;
using DeviceService;
using DeviceService.Camera;
using DeviceService.Scanner;
using DeviceService.Weight;
using Presentation_CommonLibrary.Extensions;
using Prism.Ioc;
using Serilog;
using HandyControl.Controls;
using System.Windows.Media;

namespace Presentation_SangNeng;

/// <summary>
/// 应用程序入口
/// </summary>
public partial class App
{
    private CircleProgressBar? _loadingControl;
    private System.Windows.Window? _loadingWindow;

    /// <summary>
    /// 创建主窗口
    /// </summary>
    protected override System.Windows.Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    /// <summary>
    /// 注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册通用服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddPresentationCommonServices();
        
        // 注册设备服务
        containerRegistry.AddPhotoCamera()      // 拍照相机
                        .AddVolumeCamera()      // 体积相机
                        .AddScanner()           // 扫码枪
                        .AddWeightScale();      // 重量称
        
        // 注册窗口和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.Register<System.Windows.Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
        containerRegistry.Register<System.Windows.Window, HistoryWindow>("HistoryWindow");
        containerRegistry.Register<HistoryWindowViewModel>();
        
        // 注册设置页面
        containerRegistry.Register<CameraSettingsView>();
        containerRegistry.Register<VolumeSettingsView>();
        containerRegistry.Register<WeightSettingsView>();
        containerRegistry.Register<TraySettingsView>();
        
        // 注册设置页面的ViewModel
        containerRegistry.Register<CameraSettingsViewModel>();
        containerRegistry.Register<VolumeSettingsViewModel>();
        containerRegistry.Register<WeightSettingsViewModel>();
        containerRegistry.Register<TraySettingsViewModel>();
    }

    /// <summary>
    /// 启动
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
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动");

        // 在主线程创建和显示加载窗口
        _loadingWindow = new System.Windows.Window
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

        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0)
        });

        var stackPanel = new System.Windows.Controls.StackPanel
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

        var textBlock = new System.Windows.Controls.TextBlock
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

                await Application.Current.Dispatcher.InvokeAsync(() =>
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
                await Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    scannerStartupService.StartAsync(CancellationToken.None).Wait();
                });
                
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
                await Application.Current.Dispatcher.InvokeAsync(() =>
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
            if (_loadingWindow.Content is not System.Windows.Controls.Grid grid ||
                grid.Children[1] is not System.Windows.Controls.StackPanel stackPanel) return;
            if (stackPanel.Children[1] is System.Windows.Controls.TextBlock textBlock)
            {
                textBlock.Text = message;
            }
            _loadingControl.Value = progress;
        });

        // 给UI一点时间更新
        Thread.Sleep(100);
    }

    /// <summary>
    /// 退出
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
            if (Container.Resolve<CameraFactory>() is IDisposable cameraFactory) cameraFactory.Dispose();

            // 释放相机服务
            if (Container.Resolve<ICameraService>() is IDisposable cameraService) cameraService.Dispose();

            // 释放主窗口 ViewModel
            if (MainWindow?.DataContext is IDisposable disposable) disposable.Dispose();

            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();

            // 确保所有后台线程都已完成
            Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
            Thread.Sleep(500);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}