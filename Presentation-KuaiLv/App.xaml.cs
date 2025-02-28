using System.Net.Http;
using System.Windows;
using CommonLibrary.Extensions;
using DeviceService;
using DeviceService.Camera;
using Presentation_CommonLibrary.Extensions;
using Presentation_KuaiLv.Services.DWS;
using Presentation_KuaiLv.Services.Warning;
using Presentation_KuaiLv.ViewModels;
using Presentation_KuaiLv.ViewModels.Dialogs;
using Presentation_KuaiLv.ViewModels.Settings;
using Presentation_KuaiLv.Views;
using Presentation_KuaiLv.Views.Dialogs;
using Presentation_KuaiLv.Views.Settings;
using Prism.Ioc;
using Serilog;

namespace Presentation_KuaiLv;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddPresentationCommonServices();
        containerRegistry.AddPhotoCamera();

        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<IDwsService, DwsService>();
        containerRegistry.RegisterSingleton<HttpClient>();

        // 注册警示灯服务
        containerRegistry.RegisterSingleton<IWarningLightService, WarningLightService>();
        containerRegistry.RegisterSingleton<WarningLightStartupService>();

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        // 注册设置页面
        containerRegistry.Register<CameraSettingsView>();
        containerRegistry.Register<CameraSettingsViewModel>();
        containerRegistry.Register<UploadSettingsView>();
        containerRegistry.Register<UploadSettingsViewModel>();
        containerRegistry.Register<WarningLightSettingsView>();
        containerRegistry.Register<WarningLightSettingsViewModel>();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
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
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动");
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            _ = Task.Run(async () =>
            {
                await cameraStartupService.StartAsync(CancellationToken.None);
                Log.Information("相机托管服务启动成功");
            });

            // 启动警示灯托管服务
            var warningLightStartupService = Container.Resolve<WarningLightStartupService>();
            _ = Task.Run(async () =>
            {
                await warningLightStartupService.StartAsync();
                Log.Information("警示灯托管服务启动成功");
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var tasks = new List<Task>();

            // 停止相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            tasks.Add(Task.Run(async () =>
            {
                await cameraStartupService.StopAsync(CancellationToken.None);
                Log.Information("相机托管服务已停止");
            }));

            // 停止警示灯托管服务
            var warningLightStartupService = Container.Resolve<WarningLightStartupService>();
            tasks.Add(Task.Run(async () =>
            {
                await warningLightStartupService.StopAsync(CancellationToken.None);
                Log.Information("警示灯托管服务已停止");
            }));

            // 等待所有服务停止
            await Task.WhenAll(tasks);

            // 释放主窗口 ViewModel
            if (MainWindow?.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dispose();
                Log.Information("主窗口ViewModel已释放");
            }

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

            // 确保所有后台线程都已完成
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
            await Task.Delay(500);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}