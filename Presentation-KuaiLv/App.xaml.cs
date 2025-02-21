using System.Windows;
using CommonLibrary.Extensions;
using DeviceService;
using DeviceService.Camera;
using DeviceService.Scanner;
using DeviceService.Weight;
using Presentation_CommonLibrary.Extensions;
using Presentation_KuaiLv.ViewModels;
using Presentation_KuaiLv.Views;
using Prism.Ioc;
using Serilog;

namespace Presentation_KuaiLv;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    /// <summary>
    /// 创建主窗口
    /// </summary>
    protected override Window CreateShell()
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
        // containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        // containerRegistry.Register<SettingsDialogViewModel>();
        // containerRegistry.Register<Window, HistoryWindow>("HistoryWindow");
        // containerRegistry.Register<HistoryWindowViewModel>();
        //
        // // 注册设置页面
        // containerRegistry.Register<CameraSettingsView>();
        // containerRegistry.Register<VolumeSettingsView>();
        // containerRegistry.Register<WeightSettingsView>();
        //
        // // 注册设置页面的ViewModel
        // containerRegistry.Register<CameraSettingsViewModel>();
        // containerRegistry.Register<VolumeSettingsViewModel>();
        // containerRegistry.Register<WeightSettingsViewModel>();
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
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        try
        {
            // // 启动托管服务
            // var cameraStartupService = Container.Resolve<CameraStartupService>();
            // var volumeCameraStartupService = Container.Resolve<VolumeCameraStartupService>();
            // var scannerStartupService = Container.Resolve<ScannerStartupService>();
            // var weightStartupService = Container.Resolve<WeightStartupService>();
            //
            // cameraStartupService.StartAsync(CancellationToken.None).Wait();
            // volumeCameraStartupService.StartAsync(CancellationToken.None).Wait();
            // scannerStartupService.StartAsync(CancellationToken.None).Wait();
            // weightStartupService.StartAsync(CancellationToken.None).Wait();

            Log.Information("托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
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