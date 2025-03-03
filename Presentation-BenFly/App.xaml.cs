using System.Net.Http;
using System.Windows;
using CommonLibrary.Extensions;
using CommonLibrary.Services;
using DeviceService;
using DeviceService.Camera;
using Microsoft.Extensions.DependencyInjection;
using Presentation_BenFly.Models.Upload;
using Presentation_BenFly.Services;
using Presentation_BenFly.Services.Sortings.Interfaces;
using Presentation_BenFly.Services.Sortings.Services;
using Presentation_BenFly.ViewModels.Dialogs;
using Presentation_BenFly.ViewModels.Settings;
using Presentation_BenFly.ViewModels.Windows;
using Presentation_BenFly.Views.Dialogs;
using Presentation_BenFly.Views.Settings;
using Presentation_BenFly.Views.Windows;
using Presentation_CommonLibrary.Extensions;
using Prism.Ioc;
using Serilog;
using SortSettingsView = Presentation_BenFly.Views.Settings.SortSettingsView;

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
        containerRegistry.AddPhotoCamera();

        // 注册设置页面
        containerRegistry.Register<CameraSettingsView>();
        containerRegistry.Register<SortSettingsView>();
        containerRegistry.Register<UploadSettingsView>();
        containerRegistry.Register<ChuteSettingsView>();
        containerRegistry.Register<ChuteSettingsViewModel>();

        // 注册设置页面的ViewModel
        containerRegistry.Register<CameraSettingsViewModel>();
        containerRegistry.Register<SortSettingsViewModel>();
        containerRegistry.Register<UploadSettingsViewModel>();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();

        containerRegistry.Register<IPendulumSortService, PendulumSortService>();
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
        base.OnStartup(e);

        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StartAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机托管服务时发生错误");
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StopAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务已停止");

            // 释放主窗口 ViewModel（包含摆轮分拣服务的释放）
            if (MainWindow?.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dispose();
                Log.Information("主窗口ViewModel已释放");
            }

            // 确保摆轮分拣服务已停止（双重保障）
            try
            {
                var sortService = Container.Resolve<IPendulumSortService>();
                if (sortService.IsRunning())
                {
                    sortService.StopAsync().Wait();
                    Log.Information("摆轮分拣服务已强制停止");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "强制停止摆轮分拣服务时发生错误");
            }

            // 释放相机工厂
            var cameraFactory = Container.Resolve<CameraFactory>();
            cameraFactory.DisposeAsync().AsTask().Wait();
            Log.Information("相机工厂已释放");

            // 释放相机服务
            var cameraService = Container.Resolve<ICameraService>();
            cameraService.DisposeAsync().AsTask().Wait();
            Log.Information("相机服务已释放");

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