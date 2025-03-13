using System.Windows;
using Common.Extensions;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using FuzhouPolicyForce.ViewModels;
using FuzhouPolicyForce.ViewModels.Settings;
using FuzhouPolicyForce.Views;
using FuzhouPolicyForce.Views.Settings;
using Microsoft.Extensions.Hosting;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using SortingServices.Pendulum.Models;

namespace FuzhouPolicyForce;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册设置页面的ViewModel
        containerRegistry.RegisterForNavigation<SortSettingsView, SortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<ChuteSettingsView, ChuteSettingsViewModel>();

        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();

        // 获取设置服务
        var settingsService = Container.Resolve<ISettingsService>();

        // 注册单摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(settingsService, PendulumServiceType.Single);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

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
        base.OnStartup(e);

        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StartAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务启动成功");

            // 启动摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<IHostedService>();
            pendulumHostedService.StartAsync(CancellationToken.None).Wait();
            Log.Information("摆轮分拣托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<IHostedService>();
            pendulumHostedService.StopAsync(CancellationToken.None).Wait();
            Log.Information("摆轮分拣托管服务已停止");

            // 停止相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StopAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务已停止");

            // 释放相机工厂
            var cameraFactory = Container.Resolve<CameraFactory>();
            cameraFactory.Dispose();
            Log.Information("相机工厂已释放");

            // 释放相机服务
            var cameraService = Container.Resolve<ICameraService>();
            cameraService.Dispose();
            Log.Information("相机服务已释放");

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