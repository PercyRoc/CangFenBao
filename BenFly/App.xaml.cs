using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BenFly.Services;
using BenFly.ViewModels.Dialogs;
using BenFly.ViewModels.Settings;
using BenFly.ViewModels.Windows;
using BenFly.Views.Dialogs;
using BenFly.Views.Settings;
using BenFly.Views.Windows;
using Common.Models.Settings.Sort.PendulumSort;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using System.IO;
using System.Diagnostics;
using DeviceService.DataSourceDevices.Belt;
using Timer = System.Timers.Timer;
using System.Windows.Threading;
using System.ComponentModel;
using System.Globalization;
using Common.Services.Validation;
using SharedUI.Views.Windows;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;

namespace BenFly;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\BenFly_App_Mutex";
    private Timer? _cleanupTimer;
    private bool _ownsMutex;
    private bool _isShuttingDown;
    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册主窗口
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册设置页面和ViewModel
        containerRegistry.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<UploadSettingsView, UploadSettingsViewModel>();
        containerRegistry.RegisterForNavigation<ChuteSettingsView, ChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BeltSettingsView, BeltSettingsViewModel>();

        containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>("SettingsDialog");

        // 注册串口服务
        containerRegistry.RegisterSingleton<BeltSerialService>();

        // 注册 HttpClient
        var services = new ServiceCollection();
        services.AddHttpClient("BenNiao");
        var serviceProvider = services.BuildServiceProvider();
        containerRegistry.RegisterInstance(serviceProvider.GetRequiredService<IHttpClientFactory>());

        // 注册预报数据服务
        containerRegistry.RegisterSingleton<BenNiaoPreReportService>();

        // 注册包裹回传服务
        containerRegistry.RegisterSingleton<BenNiaoPackageService>();
        
        // 注册单号校验服务
        containerRegistry.RegisterSingleton<IBarcodeValidationService, BarcodeValidationService>();
        
        // 注册单摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(PendulumServiceType.Single);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();
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

    protected override void OnStartup(StartupEventArgs e)
    {
        // Set the static instance as the default provider (optional but good practice)
        LocalizeDictionary.Instance.DefaultProvider = ResxProvider; 
        // Force English culture for testing
        var culture = new CultureInfo("zh-CN"); 
        LocalizeDictionary.Instance.Culture = culture;
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动 (OnStartup)");

        // 启动DUMP文件清理任务
        StartCleanupTask();

        // 调用基类方法初始化容器等
        base.OnStartup(e);

        // 注册全局异常处理
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        Log.Information("DispatcherUnhandledException handler attached.");
        
        // 手动启动后台服务
        StartBackgroundServices(); 
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
            await OnExitAsync(e);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用程序退出过程中发生未处理的异常");
            // 即使出错也要确保应用程序能够退出
        }
    }

    /// <summary>
    /// 应用程序退出处理的核心逻辑
    /// </summary>
    private async Task OnExitAsync(ExitEventArgs e)
    {
        Log.Information("应用程序退出处理程序 (OnExit) 开始... ExitCode: {ExitCode}", e.ApplicationExitCode);
        try
        {
            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();
             Log.Debug("清理定时器已停止。");

             // 调用基类 OnExit
             base.OnExit(e); 
        }
        catch (Exception ex)
        {
            Log.Error(ex, "调用 base.OnExit 或停止清理定时器时发生错误");
        }
        finally
        {
            Log.Information("释放 Mutex 并刷新日志... ");
            try
            {
                // 安全释放 Mutex
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        _mutex.ReleaseMutex();
                        Log.Debug("Mutex已释放");
                    }
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            { Log.Error(ex, "释放Mutex时发生错误"); }
            
            // 等待所有日志写入完成
            await Log.CloseAndFlushAsync();
        }
        Log.Information("应用程序退出处理程序 (OnExit) 完成。 ");
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

    /// <summary>
    /// 全局未处理异常处理程序
    /// </summary>
    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled Dispatcher Exception Caught by App.xaml.cs"); // 使用 Fatal 级别
        MessageBox.Show($"发生未处理的严重错误: {e.Exception.Message}\n\n应用程序可能不稳定，建议重启。请联系技术支持并提供日志文件。", "应用程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    /// 手动启动后台服务
    /// </summary>
    private void StartBackgroundServices()
    {
        Log.Information("手动启动后台服务...");
        ProgressIndicatorWindow? progressWindow = null;

        try
        {
            // 在 UI 线程显示启动进度窗口
            Current.Dispatcher.Invoke(() => 
            {
                Log.Debug("在 UI 线程上创建并显示启动进度窗口...");
                progressWindow = new ProgressIndicatorWindow("正在启动服务，请稍候...")
                {
                    Owner = Current.MainWindow 
                };
                progressWindow.Show(); 
                Log.Debug("启动进度窗口已显示。");
            });
            
            // --- 启动服务逻辑 (保持不变) ---
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            _ = Task.Run(() => cameraStartupService.StartAsync(CancellationToken.None))
                   .ContinueWith(t => Log.Error(t.Exception, "启动 CameraStartupService 时出错"), TaskContinuationOptions.OnlyOnFaulted);

            // 启动摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<PendulumSortHostedService>();
            _ = Task.Run(() => pendulumHostedService.StartAsync(CancellationToken.None))
                   .ContinueWith(t => Log.Error(t.Exception, "启动 PendulumSortHostedService 时出错"), TaskContinuationOptions.OnlyOnFaulted);

            // 获取皮带设置并启动串口托管服务（如果启用）
            var settingsService = Container.Resolve<Common.Services.Settings.ISettingsService>();
            var beltSettings = settingsService.LoadSettings<BeltSerialParams>();
            var beltSerialService = Container.Resolve<BeltSerialService>();
            if (beltSettings.IsEnabled)
            {
                beltSerialService.StartBelt();
                Log.Information("皮带控制已启用，串口托管服务启动已发起");
            }
            else
            {
                Log.Information("皮带控制已禁用，跳过串口启动");
            }
            // --- 服务启动逻辑结束 ---
            
            Log.Information("后台服务启动已全部发起。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析或发起后台服务启动时出错。可能无法启动部分或全部服务。");
            MessageBox.Show($"启动后台服务时发生错误: {ex.Message}\n请检查配置和设备连接。", "启动错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {            
            // 在 UI 线程关闭启动进度窗口
            Current.Dispatcher.InvokeAsync(() => 
            {
                progressWindow?.Close();
                Log.Debug("启动进度窗口已关闭 (如果存在)。");
            });
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Log.Information("应用程序初始化完成 (OnInitialized)");

        // 附加主窗口关闭事件处理
        var mainWindow = Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Closing += MainWindow_Closing; 
            Log.Debug("主窗口 Closing 事件处理已附加");
        }
        else
        {
            Log.Warning("无法获取主窗口实例以附加 Closing 事件处理程序。应用程序可能无法正常关闭。");
        }
    }

    /// <summary>
    /// 主窗口关闭事件处理程序
    /// </summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            await MainWindowClosingAsync(e);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "主窗口关闭过程中发生未处理的异常");
            // 确保应用程序能够关闭，即使出错也要执行shutdown
            try
            {
                Current.Shutdown();
            }
            catch (Exception shutdownEx)
            {
                Log.Fatal(shutdownEx, "强制关闭应用程序时发生异常");
                Environment.Exit(1); // 最后的手段
            }
        }
    }

    /// <summary>
    /// 主窗口关闭处理的核心逻辑
    /// </summary>
    private async Task MainWindowClosingAsync(CancelEventArgs e)
    {
        Log.Information("MainWindow_Closing 事件触发。 IsShuttingDown: {IsShuttingDown}", _isShuttingDown);
        if (_isShuttingDown)
        {
            Log.Debug("已在关闭过程中，取消本次关闭事件处理。");
            return; // 防止重入
        }

        _isShuttingDown = true;
        e.Cancel = true; // 接管关闭流程
        Log.Information("取消默认关闭，开始执行清理并显示等待窗口...");

        ProgressIndicatorWindow? progressWindow = null;
        try
        {
            // 在UI线程显示进度窗口
            await Current.Dispatcher.InvokeAsync(() =>
            {
                Log.Debug("在 UI 线程上创建并显示 ProgressIndicatorWindow...");
                progressWindow = new ProgressIndicatorWindow("正在关闭应用程序，请稍候...")
                {
                    Owner = Current.MainWindow // 设置所有者
                };
                progressWindow.Show();
                Log.Debug("ProgressIndicatorWindow 已显示。");
            });

            Log.Information("开始后台清理任务...");
            // 在后台线程停止服务并执行清理
            await Task.Run(() => StopBackgroundServices(true));
            Log.Information("后台清理任务完成。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行后台清理任务时发生错误");
            // 即使出错，也要尝试关闭
        }
        finally
        {
            Log.Information("准备关闭等待窗口并真正关闭应用程序...");
            // 在UI线程关闭进度窗口
            await Current.Dispatcher.InvokeAsync(() =>
            {
                progressWindow?.Close();
                Log.Debug("ProgressIndicatorWindow 已关闭 (如果存在)。");
            });

            // 确保在 UI 线程上调用 Shutdown
            await Current.Dispatcher.InvokeAsync(() =>
            {
                Log.Information("调用 Application.Current.Shutdown()...");
                Current.Shutdown(); // 真正关闭应用程序
            });
        }
    }

    /// <summary>
    /// 手动停止后台服务
    /// </summary>
    /// <param name="waitForCompletion">是否等待服务停止完成。</param>
    private void StopBackgroundServices(bool waitForCompletion)
    {
        Log.Information("手动停止后台服务... Wait for completion: {WaitForCompletion}", waitForCompletion);
        var tasks = new List<Task>();
        try
        {
            // 停止摆轮分拣托管服务
            var pendulumHostedService = Container?.Resolve<PendulumSortHostedService>();
            if (pendulumHostedService != null)
            {
                tasks.Add(Task.Run(() => pendulumHostedService.StopAsync(CancellationToken.None))
                           .ContinueWith(t => Log.Error(t.Exception, "停止 PendulumSortHostedService 时出错"), TaskContinuationOptions.OnlyOnFaulted));
            }

            // 停止相机托管服务
            var cameraStartupService = Container?.Resolve<CameraStartupService>();
            if (cameraStartupService != null)
            {
                tasks.Add(Task.Run(() => cameraStartupService.StopAsync(CancellationToken.None))
                           .ContinueWith(t => Log.Error(t.Exception, "停止 CameraStartupService 时出错"), TaskContinuationOptions.OnlyOnFaulted));
            }

            // 获取皮带设置并停止串口托管服务（如果启用）
            var settingsService = Container?.Resolve<Common.Services.Settings.ISettingsService>();
            var beltSettings = settingsService?.LoadSettings<BeltSerialParams>(); // Use null-conditional
            var beltSerialService = Container?.Resolve<BeltSerialService>();
            if (beltSettings is { IsEnabled: true } && beltSerialService != null)
            {
                // StopBelt 可能不是异步的
                try { beltSerialService.StopBelt(); Log.Information("皮带串口服务已停止"); }
                catch (Exception ex) { Log.Error(ex, "停止 BeltSerialService 时出错"); }
            }
            else
            {
                Log.Information("皮带控制已禁用或服务不可用，跳过串口停止");
            }
            
            // 清理相机相关资源 (移到这里确保在服务停止后执行)
            try
            {
                var cameraFactory = Container?.Resolve<CameraFactory>();
                cameraFactory?.Dispose();
                Log.Information("相机工厂已释放");

                var cameraService = Container?.Resolve<ICameraService>();
                cameraService?.Dispose();
                Log.Information("相机服务已释放");
                
                var beltService = Container?.Resolve<BeltSerialService>();
                beltService?.Dispose(); 
                Log.Information("串口服务已释放 (Dispose)");
            }
            catch(Exception ex) 
            { 
                Log.Error(ex, "清理相机或串口资源时出错"); 
            }

            if (tasks.Count != 0 && waitForCompletion)
            {
                Log.Debug("等待 {Count} 个后台服务停止任务完成...", tasks.Count);
                try
                {
                    // 等待异步服务停止完成，设置超时
                    if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(10))) // 增加超时时间
                    {
                        Log.Warning("一个或多个后台服务未在超时时间内正常停止。");
                    }
                    else
                    {
                        Log.Information("后台服务已停止 (异步部分)。");
                    }
                }
                catch (AggregateException aex)
                {
                    Log.Error(aex, "等待后台服务停止时发生聚合错误。");
                    foreach(var innerEx in aex.InnerExceptions)
                    {
                        Log.Error(innerEx, "  内部停止错误:");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待后台服务停止时发生错误。");
                }
            }
            else if (!waitForCompletion)
            {
                Log.Information("后台服务停止已发起 (不等待完成)。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析或发起后台服务停止时出错。可能无法完全停止服务。");
        }
        Log.Information("StopBackgroundServices 方法执行完毕。 ");
    }
}