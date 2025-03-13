using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Presentation_Modules.Services;
using Presentation_Modules.ViewModels;
using Presentation_Modules.ViewModels.Settings;
using Presentation_Modules.Views;
using Presentation_Modules.Views.Settings;
using Prism.DryIoc;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;

namespace Presentation_Modules;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App : PrismApplication
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

        // 注册格口映射服务
        containerRegistry.RegisterSingleton<ChuteMappingService>();

        // 注册模组连接服务
        containerRegistry.RegisterSingleton<IModuleConnectionService, ModuleConnectionService>();
        containerRegistry.RegisterSingleton<ModuleConnectionHostedService>();
        containerRegistry.Register<ModuleConnectionHostedService>();
        
        containerRegistry.Register<ModuleConfigView>();
        containerRegistry.Register<ModuleConfigViewModel>();

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

            // 启动模组连接托管服务
            var moduleConnectionHostedService = Container.Resolve<ModuleConnectionHostedService>();
            _ = Task.Run(async () =>
            {
                await moduleConnectionHostedService.StartAsync(CancellationToken.None);
                Log.Information("模组连接托管服务启动成功");
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

                // 停止模组连接托管服务
                var moduleConnectionHostedService = Container.Resolve<ModuleConnectionHostedService>();
                Task.Run(async () => await moduleConnectionHostedService.StopAsync(CancellationToken.None)).Wait(2000);
                Log.Information("模组连接托管服务已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止托管服务时发生错误");
            }

            // 释放资源
            try
            {
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
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

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