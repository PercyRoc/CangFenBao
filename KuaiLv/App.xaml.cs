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
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using System.Diagnostics;
using SharedUI.Views.Dialogs;
using Timer = System.Timers.Timer;
using Common.Services.Settings;

namespace KuaiLv;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\KuaiLv_App_Mutex";
    private bool _ownsMutex;
    private Timer? _cleanupTimer;
    internal bool IsShuttingDown;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
            return null!;
        }

        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew)
            {
                return Container.Resolve<MainWindow>();
            }

            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0);
                return null!;
            }
            else
            {
                _ownsMutex = true;
                return Container.Resolve<MainWindow>();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查应用程序实例时发生错误");
            MessageBox.Show($"启动程序时发生错误: {ex.Message}", "错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
            return null!;
        }
    }

    private static bool IsApplicationAlreadyRunning()
    {
        var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName);

        return processes.Length > 1;
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
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
        containerRegistry.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>("HistoryDialog");
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
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30)
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

        Task.Run(InitializeServicesAsync)
            .ContinueWith(task =>
            {
                if (!task.IsFaulted) return;
                Log.Error(task.Exception, "初始化服务时发生错误");
                Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"初始化服务失败，应用程序将关闭。\n\n错误: {task.Exception?.InnerException?.Message}",
                        "启动错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    Current.Shutdown();
                });
            });
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机托管服务启动成功");

            // 启动警示灯托管服务
            var settingsService = Container.Resolve<ISettingsService>();
            var warningLightConfiguration =
                settingsService.LoadSettings<Models.Settings.Warning.WarningLightConfiguration>();

            if (warningLightConfiguration.IsEnabled)
            {
                var warningLightStartupService = Container.Resolve<WarningLightStartupService>();
                await warningLightStartupService.StartAsync();
                Log.Information("警示灯托管服务启动成功");
            }
            else
            {
                Log.Information("警示灯服务未启用，跳过初始化");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动服务时发生错误");
            throw;
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
            _cleanupTimer.Elapsed += static (_, _) =>
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
            _cleanupTimer.AutoReset = true; // 确保定时器重复执行
            _cleanupTimer.Start();

            // 应用启动时立即执行一次清理
            Task.Run(static () =>
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
            throw; // 重新抛出异常，以便上层可以记录
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序开始退出...");
        try
        {
            // 停止清理定时器
            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Task.Run(async () => await Log.CloseAndFlushAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            try
            {
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        try
                        {
                            _mutex.ReleaseMutex();
                        }
                        catch (ApplicationException)
                        {
                            // 忽略可能不拥有 mutex 的情况
                        }
                    }

                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放Mutex时发生错误");
            }

            base.OnExit(e);
        }
    }

    internal async Task ShutdownServicesAsync(IContainerProvider containerProvider)
    {
        if (IsShuttingDown)
        {
            return;
        }

        IsShuttingDown = true;

        Log.Information("开始关闭服务...");

        try
        {
            // 解析服务实例
            var cameraStartupService = containerProvider.Resolve<CameraStartupService>();
            var warningLightStartupService = containerProvider.Resolve<WarningLightStartupService>();

            // 停止相机托管服务
            var cameraStopTask = cameraStartupService.StopAsync(CancellationToken.None);
            if (await Task.WhenAny(cameraStopTask, Task.Delay(TimeSpan.FromSeconds(10))) == cameraStopTask)
            {
                await cameraStopTask;
                Log.Information("相机服务已停止");
            }
            else
            {
                Log.Warning("相机服务停止超时");
            }

            // 停止警示灯托管服务
            var warningStopTask = warningLightStartupService.StopAsync();
            if (await Task.WhenAny(warningStopTask, Task.Delay(TimeSpan.FromSeconds(10))) == warningStopTask)
            {
                await warningStopTask;
                Log.Information("警示灯服务已停止");
            }
            else
            {
                Log.Warning("警示灯服务停止超时");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止服务时发生错误");
        }

        try
        {
            // 释放相机工厂
            if (containerProvider.Resolve<CameraFactory>() is IDisposable cameraFactory)
            {
                cameraFactory.Dispose();
                Log.Information("相机工厂已释放");
            }

            // 释放相机服务
            if (containerProvider.Resolve<ICameraService>() is IDisposable cameraService)
            {
                cameraService.Dispose();
                Log.Information("相机服务已释放");
            }

            // 释放DWS服务
            if (containerProvider.Resolve<IDwsService>() is IDisposable dwsService)
            {
                dwsService.Dispose();
                Log.Information("DWS服务已释放");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }

        await Log.CloseAndFlushAsync();
    }
}