using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using XiYiGu.Services;
using XiYiGu.ViewModels;
using XiYiGu.ViewModels.Settings;
using XiYiGu.Views;
using XiYiGu.Views.Settings;

namespace XiYiGu;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
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
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册HTTP客户端工厂
        containerRegistry.RegisterSingleton<IHttpClientFactory, DefaultHttpClientFactory>();
        containerRegistry.RegisterSingleton<HttpClient>();
        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();
        // 注册运单上传服务
        containerRegistry.RegisterSingleton<WaybillUploadService>();
        // 注册设置页面
        containerRegistry.Register<ApiSettingsView>();
        containerRegistry.Register<ApiSettingsViewModel>();

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
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");

            // 停止托管服务
            try
            {
                Log.Information("正在停止托管服务...");

                // 停止相机托管服务
                var cameraStartupService = Container.Resolve<CameraStartupService>();
                Task.Run(async () => await cameraStartupService.StopAsync(CancellationToken.None)).Wait(2000);
                Log.Information("相机托管服务已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止托管服务时发生错误");
            }

            // 释放资源
            try
            {
                // 释放相机工厂
                var cameraFactory = Container.Resolve<CameraFactory>();
                cameraFactory.Dispose();
                Log.Information("相机工厂已释放");

                // 释放相机服务
                var cameraService = Container.Resolve<ICameraService>();
                cameraService.Dispose();
                Log.Information("相机服务已释放");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();

            // 确保所有操作完成
            Thread.Sleep(1000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
            Thread.Sleep(1000);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}