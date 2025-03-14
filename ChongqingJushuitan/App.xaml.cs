using System.Net.Http;
using System.Windows;
using ChongqingJushuitan.Services;
using ChongqingJushuitan.ViewModels;
using ChongqingJushuitan.Views;
using ChongqingJushuitan.Views.Settings;
using ChongqingJushuitan.ViewModels.Settings;
using Common.Extensions;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using FuzhouPolicyForce.Views.Settings;
using Microsoft.Extensions.Hosting;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using SortingServices.Pendulum.Models;

namespace ChongqingJushuitan;

/// <summary>
/// Interaction logic for App.xaml
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
        
        // 注册聚水潭服务
        containerRegistry.RegisterSingleton<IJuShuiTanService, JuShuiTanService>();
        
        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
        
        // 注册聚水潭设置页面
        containerRegistry.RegisterForNavigation<JushuitanSettingsPage, JushuitanSettingsViewModel>();
        
        // 获取设置服务
        var settingsService = Container.Resolve<ISettingsService>();

        // 注册多摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(settingsService, PendulumServiceType.Multi);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();
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

                // 停止摆轮分拣托管服务
                var pendulumHostedService = Container.Resolve<IHostedService>();
                pendulumHostedService.StopAsync(CancellationToken.None).Wait();
                Log.Information("摆轮分拣托管服务已停止");

                // 停止相机托管服务
                var cameraStartupService = Container.Resolve<CameraStartupService>();
                cameraStartupService.StopAsync(CancellationToken.None).Wait();
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

                // 释放摆轮分拣服务
                if (Container.Resolve<IPendulumSortService>() is IDisposable pendulumService)
                {
                    pendulumService.Dispose();
                    Log.Information("摆轮分拣服务已释放");
                }
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