using System.Net.Http;
using System.Windows;
using CommonLibrary.Extensions;
using CommonLibrary.Services;
using DeviceService;
using DeviceService.Camera;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation_BenFly.Models.Upload;
using Presentation_BenFly.Services;
using Presentation_BenFly.ViewModels.Dialogs;
using Presentation_BenFly.ViewModels.Settings;
using Presentation_BenFly.ViewModels.Windows;
using Presentation_BenFly.Views.Dialogs;
using Presentation_BenFly.Views.Settings;
using Presentation_BenFly.Views.Windows;
using Presentation_CommonLibrary.Extensions;
using Prism.Ioc;
using Serilog;
using SortingService.Extensions;

namespace Presentation_BenFly;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddPresentationCommonServices();

        // 注册设备服务
        containerRegistry.AddDeviceServices();

        containerRegistry.AddSortingServices();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();

        // 注册设置页面
        containerRegistry.Register<CameraSettingsView>();
        containerRegistry.Register<SortSettingsView>();
        containerRegistry.Register<UploadSettingsView>();

        // 注册设置页面的ViewModel
        containerRegistry.Register<CameraSettingsViewModel>();
        containerRegistry.Register<SortSettingsViewModel>();
        containerRegistry.Register<UploadSettingsViewModel>();

        // 注册 HttpClient
        var services = new ServiceCollection();
        var settingsService = Container.Resolve<ISettingsService>();
        var config = settingsService.LoadSettings<UploadConfiguration>("UploadSettings");
        var baseUrl = config.BenNiaoEnvironment == BenNiaoEnvironment.Production
            ? "https://api.benniao.com"
            : "http://sit.bnsy.rhb56.cn";

        services.AddHttpClient("BenNiao", client => { client.BaseAddress = new Uri(baseUrl); });
        var serviceProvider = services.BuildServiceProvider();
        containerRegistry.RegisterInstance(serviceProvider.GetRequiredService<IHttpClientFactory>());

        // 注册预报数据服务
        containerRegistry.RegisterSingleton<BenNiaoPreReportService>();

        // 注册包裹回传服务
        containerRegistry.RegisterSingleton<BenNiaoPackageService>();
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
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        try
        {
            // 启动托管服务
            var hostedService = Container.Resolve<IHostedService>();
            hostedService.StartAsync(CancellationToken.None).Wait();
            Log.Information("托管服务启动成功");
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
            // 停止托管服务
            var hostedService = Container.Resolve<IHostedService>();
            hostedService.StopAsync(CancellationToken.None).Wait();

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