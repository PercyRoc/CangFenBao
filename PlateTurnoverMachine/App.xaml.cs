using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Microsoft.Extensions.Hosting;
using Presentation_PlateTurnoverMachine.Models;
using Presentation_PlateTurnoverMachine.Services;
using Presentation_PlateTurnoverMachine.ViewModels;
using Presentation_PlateTurnoverMachine.ViewModels.Settings;
using Presentation_PlateTurnoverMachine.Views;
using Presentation_PlateTurnoverMachine.Views.Settings;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;

namespace Presentation_PlateTurnoverMachine;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<PlateTurnoverSettingsView, PlateTurnoverSettingsViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<HttpClient>();

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();

        // 注册TCP连接服务
        containerRegistry.RegisterSingleton<ITcpConnectionService, TcpConnectionService>();
        containerRegistry.RegisterSingleton<SortingService>();
        containerRegistry.RegisterSingleton<PlateTurnoverSettings>();
        containerRegistry.RegisterSingleton<TcpConnectionHostedService>();
        containerRegistry.Register<IHostedService>(sp => sp.Resolve<TcpConnectionHostedService>());
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
            // 启动托管服务
            var hostedService = Container.Resolve<TcpConnectionHostedService>();
            await hostedService.StartAsync(CancellationToken.None);

            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动服务时发生错误");
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止托管服务
            var hostedService = Container.Resolve<TcpConnectionHostedService>();
            await hostedService.StopAsync(CancellationToken.None);

            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StopAsync(CancellationToken.None);
            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            await Log.CloseAndFlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            await Log.CloseAndFlushAsync();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}