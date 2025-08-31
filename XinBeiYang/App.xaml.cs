using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.DataSourceDevices.Weight;
using DeviceService.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using SharedUI.Extensions;
using System.Windows.Media;
using XinBeiYang.Services;
using XinBeiYang.ViewModels;
using XinBeiYang.ViewModels.Settings;
using XinBeiYang.Views;
using XinBeiYang.Views.Settings;
// using XinBeiYang.Logging; // 已移除MemoryRingBufferSink引用
// using XinBeiYang.Diagnostics; // 已移除GlobalExceptionHandler相关功能
using Timer = System.Timers.Timer;

namespace XinBeiYang;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = "Global\\XinBeiYang_App_Mutex";
    private static Mutex? _mutex;
    private Timer? _cleanupTimer;
    private bool _ownsMutex;

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

            if (createdNew) return Container.Resolve<MainWindow>();

            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0);
                return null!;
            }

            _ownsMutex = true;
            return Container.Resolve<MainWindow>();
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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();
        containerRegistry.RegisterInstance<IConfiguration>(configuration);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .CreateLogger();
        Log.Information("Serilog 已根据 appsettings.json 配置.");

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
        Log.Information("应用程序启动 (OnStartup)");

        // 读取配置（渲染模式）
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();

        // 渲染模式：默认自动（硬件优先）；如配置 Rendering:ForceSoftwareOnly=true 则强制软件渲染
        var forceSoftware = bool.TryParse(config["Rendering:ForceSoftwareOnly"], out var f) && f;
        RenderOptions.ProcessRenderMode = forceSoftware
            ? System.Windows.Interop.RenderMode.SoftwareOnly
            : System.Windows.Interop.RenderMode.Default;
        var tier = (RenderCapability.Tier >> 16);
        Log.Information("WPF Render Tier={Tier}, Mode={Mode}, ForceSoftwareOnly={Force}", tier,
            RenderOptions.ProcessRenderMode, forceSoftware);

        StartCleanupTask();

        // GlobalExceptionHandler 已完全删除，避免AccessViolationException

        base.OnStartup(e);

        // 在UI线程中同步启动服务，避免线程模型问题
        try
        {
            InitializeServicesAsync().ContinueWith(task =>
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
        catch (Exception ex)
        {
            Log.Error(ex, "启动服务初始化时发生同步异常");
            MessageBox.Show(
                $"启动服务时发生错误，应用程序将关闭。\n\n错误: {ex.Message}",
                "启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            // 1. 在UI线程中同步启动重量称服务，确保线程模型正确
            var weightService = Container.Resolve<SerialPortWeightService>();
            try
            {
                // 直接在当前线程（UI线程）启动重量称服务
                Log.Information("正在UI线程中启动重量称服务...");
                var weightStartResult = weightService.Start();

                if (weightStartResult)
                {
                    Log.Information("重量称服务启动成功 (在UI线程中)");
                }
                else
                {
                    Log.Warning("重量称服务启动失败，返回false");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在UI线程中启动重量称服务时发生错误");
                // 重量称服务失败不应该阻止其他服务启动
            }

            // 2. 启动相机服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机托管服务启动成功");

            // 3. 启动PLC通信服务
            var hostedService = Container.Resolve<PlcCommunicationHostedService>();
            await hostedService.StartAsync(CancellationToken.None);

            // 4. 启动JD WCS通信服务
            var jdWcsService = Container.Resolve<IJdWcsCommunicationService>();
            jdWcsService.Start();
            Log.Information("JD WCS通信服务启动成功");

            Log.Information("所有服务启动完成");
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
            try
            {
                Log.Error(ex, "应用程序关闭时发生错误");
                Log.CloseAndFlush();
            }
            catch
            {
                /* ignored */
            }
        }
        finally
        {
            // 释放 Mutex 的逻辑保持不变
            try
            {
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                        try
                        {
                            _mutex.ReleaseMutex();
                            Log.Information("Mutex已释放");
                        }
                        catch
                        {
                            /* ignored */
                        }

                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Log.Error(ex, "释放Mutex时发生错误");
                    Log.CloseAndFlush();
                }
                catch
                {
                    /* ignored */
                }
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
            try
            {
                var weightService = Container.Resolve<SerialPortWeightService>();
                weightService.Stop();
                weightService.Dispose();
                Log.Information("重量称服务已停止 (直接通过 SerialPortWeightService)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止重量称服务时发生错误（直接通过 SerialPortWeightService）");
            }

            // 停止PLC托管服务
            var hostedService = Container.Resolve<PlcCommunicationHostedService>();
            await hostedService.StopAsync(CancellationToken.None);
            Log.Information("PLC托管服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止托管服务时发生错误");
        }
    }
}