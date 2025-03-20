using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using KuaiLv.Services.DWS;
using KuaiLv.Services.Warning;
using KuaiLv.ViewModels;
using KuaiLv.ViewModels.Dialogs;
using KuaiLv.ViewModels.Settings;
using KuaiLv.Views;
using KuaiLv.Views.Dialogs;
using KuaiLv.Views.Settings;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using Timer = System.Timers.Timer;

namespace KuaiLv;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "KuaiLv_App_Mutex";
    private Timer? _cleanupTimer;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew) return Container.Resolve<MainWindow>();

        // 关闭当前实例
        Current.Shutdown();
        return null!;
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

        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<IDwsService, DwsService>();
        containerRegistry.RegisterSingleton<HttpClient>();
        containerRegistry.RegisterSingleton<OfflinePackageService>();

        // 注册警示灯服务
        containerRegistry.RegisterSingleton<IWarningLightService, WarningLightService>();
        containerRegistry.RegisterSingleton<WarningLightStartupService>();

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

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

        // 注册全局异常处理
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // 启动DUMP文件清理任务
        StartCleanupTask();

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
            Log.Error(ex, "启动服务时发生错误");
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Log.Fatal(e.Exception, "UI线程发生未处理的异常");
            MessageBox.Show($"程序发生错误：{e.Exception.Message}\n请查看日志了解详细信息。", "错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理UI异常时发生错误");
        }
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var exception = e.ExceptionObject as Exception;
            Log.Fatal(exception, "应用程序域发生未处理的异常");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理应用程序域异常时发生错误");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log.Fatal(e.Exception, "异步任务中发生未处理的异常");
            e.SetObserved();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理异步任务异常时发生错误");
        }
    }

    /// <summary>
    ///     启动定期清理任务
    /// </summary>
    private void StartCleanupTask()
    {
        try
        {
            _cleanupTimer = new Timer(1000 * 60 * 60); // 每1小时执行一次
            _cleanupTimer.Elapsed += (_, _) =>
            {
                try
                {
                    CleanupDumpFiles();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "清理DUMP文件时发生错误");
                }
            };
            _cleanupTimer.Start();

            // 应用启动时立即执行一次清理
            Task.Run(() =>
            {
                try
                {
                    CleanupDumpFiles();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "初始清理DUMP文件时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动清理任务时发生错误");
        }
    }

    /// <summary>
    ///     清理DUMP文件
    /// </summary>
    private static void CleanupDumpFiles()
    {
        try
        {
            var dumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
            var dumpFiles = Directory.GetFiles(dumpPath, "*.dmp", SearchOption.TopDirectoryOnly);

            var deletedCount = 0;
            foreach (var file in dumpFiles)
                try
                {
                    File.Delete(file);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除DUMP文件失败: {FilePath}", file);
                }

            if (deletedCount > 0) Log.Information("成功清理 {Count} 个DUMP文件", deletedCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理DUMP文件过程中发生错误");
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

                // 停止警示灯托管服务
                var warningLightStartupService = Container.Resolve<WarningLightStartupService>();
                Task.Run(warningLightStartupService.StopAsync).Wait(2000);

                Log.Information("托管服务已停止");
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
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}