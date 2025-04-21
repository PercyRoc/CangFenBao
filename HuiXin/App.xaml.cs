using System.Diagnostics;
using System.IO;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using HuiXin.ViewModels;
using HuiXin.ViewModels.Dialogs;
using HuiXin.Views;
using HuiXin.Views.Dialogs;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using SortingServices.Car;
using SortingServices.Servers.Services.JuShuiTan;
using Timer = System.Timers.Timer;

namespace HuiXin;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\HuiXin_App_Mutex";
    private Timer? _cleanupTimer;
    private bool _ownsMutex;

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册主窗口
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册需要共享的设置为单例
        containerRegistry.RegisterSingleton<CarConfigViewModel>();
        containerRegistry.RegisterSingleton<CarSequenceViewModel>();
        containerRegistry.RegisterSingleton<BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterSingleton<SerialPortSettingsViewModel>();
        containerRegistry.RegisterSingleton<JushuitanSettingsViewModel>();

        // 注册设置页面导航 (这会使用上面定义的单例注册)
        containerRegistry.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<CarConfigView, CarConfigViewModel>();
        containerRegistry.RegisterForNavigation<CarSequenceView, CarSequenceViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<SerialPortSettingsView, SerialPortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<JushuitanSettingsPage, JushuitanSettingsViewModel>();

        containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>("SettingsDialog");
        containerRegistry.RegisterSingleton<IJuShuiTanService, JuShuiTanService>();
        containerRegistry.RegisterSingleton<ICarSortService, CarSortService>();
        containerRegistry.RegisterSingleton<ICarSortingService, CarSortingService>();
    }

    protected override Window CreateShell()
    {
        // 检查是否已经运行（进程级检查）
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0); // 直接退出进程
            return null!;
        }

        try
        {
            // 尝试创建全局Mutex
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew)
            {
                return Container.Resolve<MainWindow>();
            }

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0); // 直接退出进程
                return null!; // 虽然不会执行到这里，但需要满足返回类型
            }
            else
            {
                // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
                _ownsMutex = true;
                return Container.Resolve<MainWindow>();
            }
        }
        catch (Exception ex)
        {
            // Mutex创建或获取失败
            Log.Error(ex, "检查应用程序实例时发生错误");
            MessageBox.Show($"启动程序时发生错误: {ex.Message}", "错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
            return null!;
        }
    }

    /// <summary>
    /// 检查是否已有相同名称的应用程序实例在运行
    /// </summary>
    private static bool IsApplicationAlreadyRunning()
    {
        var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName);

        // 当前进程也会被计入，所以如果数量大于1则说明有其他实例
        return processes.Length > 1;
    }

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

        // 启动DUMP文件清理任务
        StartCleanupTask();
        base.OnStartup(e);
        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
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
            throw;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止清理定时器
            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();

            // 停止相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StopAsync(CancellationToken.None);
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
            await Log.CloseAndFlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            await Log.CloseAndFlushAsync();
        }
        finally
        {
            try
            {
                // 安全释放 Mutex
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        _mutex.ReleaseMutex();
                        Log.Information("Mutex已释放");
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
}