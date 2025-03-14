using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Presentation_SeedingWall.Services;
using Presentation_SeedingWall.ViewModels;
using Presentation_SeedingWall.ViewModels.Settings;
using Presentation_SeedingWall.Views;
using Presentation_SeedingWall.Views.Settings;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;

namespace Presentation_SeedingWall;

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
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        containerRegistry.RegisterSingleton<HttpClient>();
        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        // 注册聚水潭WebSocket服务
        containerRegistry.RegisterSingleton<IJuShuiTanService, JuShuiTanService>();
        containerRegistry.RegisterSingleton<JuShuiTanStartupService>();

        // 注册PLC服务
        containerRegistry.RegisterSingleton<IPlcService, PlcService>();
        containerRegistry.RegisterSingleton<PlcStartupService>();

        // 注册设置页面
        containerRegistry.Register<JuShuiTanSettingsView>();
        containerRegistry.Register<JuShuiTanSettingsViewModel>();
        containerRegistry.Register<PlcSettingsView>();
        containerRegistry.Register<PlcSettingsViewModel>();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
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

            // 启动聚水潭托管服务
            var juShuiTanStartupService = Container.Resolve<JuShuiTanStartupService>();
            await juShuiTanStartupService.StartAsync(CancellationToken.None);
            Log.Information("聚水潭托管服务已启动");

            // 启动PLC服务
            var plcStartupService = Container.Resolve<PlcStartupService>();
            await plcStartupService.StartAsync(CancellationToken.None);
            Log.Information("PLC服务已启动");
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

                // 停止聚水潭托管服务
                var juShuiTanStartupService = Container.Resolve<JuShuiTanStartupService>();
                juShuiTanStartupService.StopAsync(CancellationToken.None).Wait();
                Log.Information("聚水潭托管服务已停止");

                // 停止PLC服务
                var plcStartupService = Container.Resolve<PlcStartupService>();
                plcStartupService.StopAsync(CancellationToken.None).Wait();
                Log.Information("PLC服务已停止");

                // 断开聚水潭WebSocket连接
                var juShuiTanService = Container.Resolve<IJuShuiTanService>();
                juShuiTanService.Disconnect();
                Log.Information("聚水潭WebSocket连接已断开");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止托管服务时发生错误");
            }

            // 释放资源
            try
            {
                var cameraFactory = Container.Resolve<CameraFactory>();
                cameraFactory.Dispose();
                Log.Information("相机工厂已释放");

                // 释放相机服务
                var cameraService = Container.Resolve<ICameraService>();
                cameraService.Dispose();
                Log.Information("相机服务已释放");

                // 释放聚水潭服务
                Container.Resolve<IJuShuiTanService>().Dispose();
                Log.Information("聚水潭服务已释放");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
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
            base.OnExit(e);
        }
    }
}