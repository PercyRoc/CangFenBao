using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.DataSourceDevices.Weight;
using DeviceService.Extensions;
using Microsoft.Extensions.Hosting;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using XinBeiYang.Services;
using XinBeiYang.ViewModels;
using XinBeiYang.ViewModels.Settings;
using XinBeiYang.Views;
using XinBeiYang.Views.Settings;
using System.Diagnostics;
using System.IO;
using Timer = System.Timers.Timer;

namespace XinBeiYang;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\XinBeiYang_App_Mutex";
    private bool _ownsMutex;
    private Timer? _cleanupTimer;

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

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<HostSettingsView, HostSettingsViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();
        containerRegistry.AddWeightScale();

        containerRegistry.RegisterSingleton<HttpClient>();

        containerRegistry.RegisterSingleton<PackageTransferService>();

        containerRegistry.RegisterSingleton<IPlcCommunicationService, PlcCommunicationService>();

        containerRegistry.RegisterSingleton<IHostedService, PlcCommunicationHostedService>();

        containerRegistry.RegisterSingleton<IJdWcsCommunicationService, JdWcsCommunicationService>();

        containerRegistry.RegisterSingleton<IImageStorageService, LocalImageStorageService>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30)
            .CreateLogger();

        Log.Information("应用程序启动");
        
        StartCleanupTask();

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "在应用程序中捕获到未观察的任务异常");
            args.SetObserved();
        };

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
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机托管服务启动成功");
            
            var weightStartupService = Container.Resolve<WeightStartupService>();
            await weightStartupService.StartAsync(CancellationToken.None);
            Log.Information("重量称服务启动成功");
            
            var hostedService = Container.Resolve<PlcCommunicationHostedService>();
            await hostedService.StartAsync(CancellationToken.None);
            
            var jdWcsService = Container.Resolve<JdWcsCommunicationService>();
            jdWcsService.Start();
            Log.Information("京东WCS通信服务启动成功");
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 停止清理定时器
            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();

            // 异步执行关闭操作，不阻塞主线程
            // 主线程退出时，DI容器应负责调用单例服务的Dispose
            _ = ShutdownServicesAsync(); 
            // 等待日志刷新完成
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            // 尝试记录最后的错误
            try { Log.Error(ex, "应用程序关闭时发生错误"); Log.CloseAndFlush(); } catch { /* ignored */ }
        }
        finally
        {
            // 释放 Mutex 的逻辑保持不变
            try
            {
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        try { _mutex.ReleaseMutex(); Log.Information("Mutex已释放"); } catch { /* ignored */ }
                    }
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                try { Log.Error(ex, "释放Mutex时发生错误"); Log.CloseAndFlush(); } catch { /* ignored */ }
            }
            base.OnExit(e);
        }
    }

    private async Task ShutdownServicesAsync()
    {
        Log.Information("应用程序开始关闭...");

        try
        {
            Log.Information("正在停止托管服务...");

            // 停止相机服务（假设它也是类似托管服务）
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StopAsync(CancellationToken.None);
            Log.Information("相机托管服务已停止");
            
            // 停止重量称服务
            var weightStartupService = Container.Resolve<WeightStartupService>();
            await weightStartupService.StopAsync(CancellationToken.None);
            Log.Information("重量称服务已停止");

            // 停止PLC托管服务
            var hostedService = Container.Resolve<PlcCommunicationHostedService>();
            await hostedService.StopAsync(CancellationToken.None);
            Log.Information("PLC托管服务已停止");
            
            // 移除手动停止 JdWcsCommunicationService 的调用
            // 让 DI 容器在程序退出时调用其 Dispose 方法
            // var jdWcsService = Container.Resolve<JdWcsCommunicationService>();
            // await jdWcsService.StopAsync();
            // Log.Information("京东WCS通信服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止托管服务时发生错误");
        }
    }
}