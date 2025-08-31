using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BenFly.Services;
using BenFly.ViewModels.Dialogs;
using BenFly.ViewModels.Settings;
using BenFly.ViewModels.Windows;
using BenFly.Views.Dialogs;
using BenFly.Views.Settings;
using BenFly.Views.Windows;
using Common.Extensions;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using Common.Services.Validation;
using DeviceService.DataSourceDevices.Belt;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Dialogs;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Dialogs;
using SharedUI.Views.Settings;
using SharedUI.Views.Windows;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;
using Timer = System.Timers.Timer;

namespace BenFly;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = "Global\\BenFly_App_Mutex";
    private static Mutex? _mutex;
    private Timer? _cleanupTimer;
    private bool _isShuttingDown;
    private bool _ownsMutex;
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
        containerRegistry.RegisterDialog<CalibrationDialogView, CalibrationDialogViewModel>("CalibrationDialog");

        // 注册串口服务
        containerRegistry.RegisterSingleton<BeltSerialService>();
        containerRegistry.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>("HistoryDialog");
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();

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
        containerRegistry.RegisterSingleton<PendulumSortService>();
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

            if (createdNew) return Container.Resolve<MainWindow>();

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0); // 直接退出进程
                return null!; // 虽然不会执行到这里，但需要满足返回类型
            }

            // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
            _ownsMutex = true;
            return Container.Resolve<MainWindow>();
        }
        catch (Exception ex)
        {
            // Mutex创建或获取失败
            LogUnhandledException(ex, "Mutex Creation/Check Error");
            MessageBox.Show($"Startup error: {ex.Message}", "Error", MessageBoxButton.OK,
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
        AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;
        TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;
        Log.Information("全局异常处理程序已注册 (DispatcherUnhandledException, UnhandledException, UnobservedTaskException)");

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
            LogUnhandledException(ex, "Application Exit Error", true);
            // 即使出错也要确保应用程序能够退出
        }
    }

    /// <summary>
    ///     应用程序退出处理的核心逻辑
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
            LogUnhandledException(ex, "Base OnExit or Timer Stop Error");
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
            {
                LogUnhandledException(ex, "Mutex Release Error");
            }

            // 等待所有日志写入完成
            await Log.CloseAndFlushAsync();
        }

        Log.Information("应用程序退出处理程序 (OnExit) 完成。 ");
    }

    /// <summary>
    ///     检查是否已有相同名称的应用程序实例在运行
    /// </summary>
    private static bool IsApplicationAlreadyRunning()
    {
        var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName);

        // 当前进程也会被计入，所以如果数量大于1则说明有其他实例
        return processes.Length > 1;
    }

    /// <summary>
    ///     全局未处理异常处理程序 (UI线程)
    /// </summary>
    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception, "Unhandled Dispatcher Exception");
        MessageBox.Show(
            $"An unhandled error occurred: {e.Exception.Message}\n\nApplication may be unstable. Please restart and contact technical support with log files.",
            "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    ///     应用程序域未处理异常处理程序 (非UI线程)
    /// </summary>
    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ??
                        new Exception($"Unknown exception object: {e.ExceptionObject}");
        LogUnhandledException(exception, "Unhandled AppDomain Exception", e.IsTerminating);

        if (e.IsTerminating)
            // 应用程序即将终止，尝试显示错误信息
            try
            {
                MessageBox.Show(
                    $"A fatal error occurred: {exception.Message}\n\nApplication will terminate. Please contact technical support with log files.",
                    "Fatal Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 如果无法显示MessageBox，忽略错误
            }
    }

    /// <summary>
    ///     Task未观察异常处理程序
    /// </summary>
    private static void App_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception, "Unobserved Task Exception");
        e.SetObserved(); // 标记异常已被观察，防止应用程序崩溃
    }

    /// <summary>
    ///     统一的异常日志记录方法，包含备份日志记录机制
    /// </summary>
    private static void LogUnhandledException(Exception exception, string source, bool isTerminating = false)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source} - IsTerminating: {isTerminating}\n" +
                         $"Exception Type: {exception.GetType().FullName}\n" +
                         $"Message: {exception.Message}\n" +
                         $"StackTrace:\n{exception.StackTrace}\n";

        if (exception.InnerException != null)
            logMessage += $"Inner Exception: {exception.InnerException.GetType().FullName}\n" +
                          $"Inner Message: {exception.InnerException.Message}\n" +
                          $"Inner StackTrace:\n{exception.InnerException.StackTrace}\n";

        logMessage += new string('=', 80) + "\n";

        // 尝试使用Serilog记录异常
        try
        {
            Log.Fatal(exception, "{Source} - IsTerminating: {IsTerminating}", source, isTerminating);
        }
        catch
        {
            // Serilog记录失败，使用备份日志
        }

        // 备份日志记录机制 - 直接写入文件
        WriteToBackupLog(logMessage);
    }

    /// <summary>
    ///     备份日志记录方法，当Serilog无法工作时使用
    /// </summary>
    private static void WriteToBackupLog(string logMessage)
    {
        try
        {
            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);

            var backupLogFile = Path.Combine(logDirectory, $"unhandled-exceptions-{DateTime.Now:yyyy-MM-dd}.log");

            // 使用FileStream确保线程安全的写入
            using var fileStream = new FileStream(backupLogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fileStream, Encoding.UTF8);
            writer.WriteLine(logMessage);
            writer.Flush();
        }
        catch
        {
            // 如果备份日志也无法写入，尝试写入Windows事件日志
            try
            {
                var eventLog = new EventLog("Application")
                {
                    Source = "BenFly Application"
                };
                eventLog.WriteEntry($"Failed to write to backup log. Original message:\n{logMessage}",
                    EventLogEntryType.Error);
            }
            catch
            {
                // 最后的手段：尝试写入临时文件
                try
                {
                    var tempLogFile = Path.Combine(Path.GetTempPath(),
                        $"BenFly_CriticalError_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    File.WriteAllText(tempLogFile, logMessage, Encoding.UTF8);
                }
                catch
                {
                    // 如果所有方法都失败，只能放弃记录
                }
            }
        }
    }

    /// <summary>
    ///     手动启动后台服务
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
                .ContinueWith(t => LogUnhandledException(t.Exception!, "CameraStartupService Start Error"),
                    TaskContinuationOptions.OnlyOnFaulted);

            // 启动摆轮分拣服务
            var pendulumService = Container.Resolve<PendulumSortService>();
            _ = Task.Run(() => pendulumService.StartAsync())
                .ContinueWith(t => LogUnhandledException(t.Exception!, "PendulumSortService Start Error"),
                    TaskContinuationOptions.OnlyOnFaulted);

            // 获取皮带设置并启动串口托管服务（如果启用）
            var settingsService = Container.Resolve<ISettingsService>();
            var beltSettings = settingsService.LoadSettings<BeltSerialParams>();
            var beltSerialService = Container.Resolve<BeltSerialService>();
            if (beltSettings.IsEnabled)
            {
                _ = beltSerialService.StartBelt();
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
            LogUnhandledException(ex, "Background Services Startup Error");
            MessageBox.Show(
                $"Error starting background services: {ex.Message}\n\nPlease check configuration and device connections.",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    ///     主窗口关闭事件处理程序
    /// </summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            await MainWindowClosingAsync(e);
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex, "MainWindow Closing Error", true);
            // 确保应用程序能够关闭，即使出错也要执行shutdown
            try
            {
                Current.Shutdown();
            }
            catch (Exception shutdownEx)
            {
                LogUnhandledException(shutdownEx, "Forced Shutdown Error", true);
                Environment.Exit(1); // 最后的手段
            }
        }
    }

    /// <summary>
    ///     主窗口关闭处理的核心逻辑
    /// </summary>
    private async Task MainWindowClosingAsync(CancelEventArgs e)
    {
        Log.Information("MainWindow_Closing 事件触发。 IsShuttingDown: {IsShuttingDown}, EventCanceled: {EventCanceled}",
            _isShuttingDown, e.Cancel);

        // 如果关闭事件已被取消（用户点击了"否"），则不执行关闭流程
        if (e.Cancel)
        {
            Log.Information("关闭操作已被用户取消，停止执行关闭流程");
            return;
        }

        if (_isShuttingDown)
        {
            Log.Debug("已在关闭过程中，取消本次关闭事件处理。");
            return; // 防止重入
        }

        _isShuttingDown = true;
        e.Cancel = true; // 接管关闭流程
        Log.Information("用户确认关闭，开始执行清理流程...");

        try
        {
            Log.Information("开始清理任务...");

            // 首先释放MainWindowViewModel
            try
            {
                if (Current.MainWindow?.DataContext is IDisposable viewModel)
                    await Task.Run(() =>
                    {
                        try
                        {
                            viewModel.Dispose();
                            Log.Information("主窗口ViewModel已释放");
                        }
                        catch (Exception ex)
                        {
                            LogUnhandledException(ex, "ViewModel Dispose Error");
                        }
                    });
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "ViewModel Access Error");
            }

            // 然后停止后台服务
            await Task.Run(() =>
            {
                try
                {
                    StopBackgroundServices(true);
                    Log.Information("后台服务停止完成");
                }
                catch (Exception ex)
                {
                    LogUnhandledException(ex, "StopBackgroundServices Error");
                }
            }).ConfigureAwait(false);

            Log.Information("所有清理任务完成。");
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex, "Background Cleanup Error");
            // 即使出错，也要尝试关闭
        }
        finally
        {
            try
            {
                Log.Information("准备真正关闭应用程序...");
                // 确保在 UI 线程上调用 Shutdown
                await Current.Dispatcher.InvokeAsync(() =>
                {
                    Log.Information("调用 Application.Current.Shutdown()...");
                    Current.Shutdown(); // 真正关闭应用程序
                });
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "Final Shutdown Error");
                // 最后的保险措施
                Environment.Exit(0);
            }
        }
    }

    /// <summary>
    ///     手动停止后台服务
    /// </summary>
    /// <param name="waitForCompletion">是否等待服务停止完成。</param>
    private void StopBackgroundServices(bool waitForCompletion)
    {
        Log.Information("=== 开始手动停止后台服务... Wait for completion: {WaitForCompletion} ===", waitForCompletion);
        var tasks = new List<Task>();
        try
        {
            Log.Information("正在解析容器中的服务...");

            // 停止摆轮分拣服务
            try
            {
                var pendulumService = Container?.Resolve<PendulumSortService>();
                if (pendulumService != null)
                {
                    Log.Information("找到摆轮分拣服务，正在停止...");
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await pendulumService.StopAsync();
                            Log.Information("摆轮分拣服务已停止");
                        }
                        catch (Exception ex)
                        {
                            LogUnhandledException(ex, "PendulumSortService Stop Error");
                        }
                    }));
                }
                else
                {
                    Log.Warning("未找到摆轮分拣服务");
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "PendulumSortService Resolve Error");
            }

            // 停止相机托管服务
            try
            {
                var cameraStartupService = Container?.Resolve<CameraStartupService>();
                if (cameraStartupService != null)
                {
                    Log.Information("找到相机托管服务，正在停止...");
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await cameraStartupService.StopAsync(CancellationToken.None);
                            Log.Information("相机托管服务已停止");
                        }
                        catch (Exception ex)
                        {
                            LogUnhandledException(ex, "CameraStartupService Stop Error");
                        }
                    }));
                }
                else
                {
                    Log.Warning("未找到相机托管服务");
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "CameraStartupService Resolve Error");
            }

            // 获取皮带设置并停止串口托管服务（如果启用）
            try
            {
                var settingsService = Container?.Resolve<ISettingsService>();
                var beltSettings = settingsService?.LoadSettings<BeltSerialParams>();
                var beltSerialService = Container?.Resolve<BeltSerialService>();

                if (beltSettings is { IsEnabled: true } && beltSerialService != null)
                {
                    Log.Information("皮带控制已启用，正在停止串口服务...");
                    try
                    {
                        beltSerialService.StopBelt();
                        Log.Information("皮带串口服务已停止");
                    }
                    catch (Exception ex)
                    {
                        LogUnhandledException(ex, "BeltSerialService Stop Error");
                    }
                }
                else
                {
                    Log.Information("皮带控制已禁用或服务不可用，跳过串口停止");
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "Belt Service Resolve/Stop Error");
            }

            // 清理相机相关资源 (移到这里确保在服务停止后执行)
            Log.Information("开始清理相机和串口资源...");
            try
            {
                var cameraFactory = Container?.Resolve<CameraFactory>();
                if (cameraFactory != null)
                {
                    cameraFactory.Dispose();
                    Log.Information("相机工厂已释放");
                }

                var cameraService = Container?.Resolve<ICameraService>();
                if (cameraService != null)
                {
                    cameraService.Dispose();
                    Log.Information("相机服务已释放");
                }

                var beltService = Container?.Resolve<BeltSerialService>();
                if (beltService != null)
                {
                    beltService.Dispose();
                    Log.Information("串口服务已释放 (Dispose)");
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException(ex, "Camera/Serial Resources Cleanup Error");
            }

            if (tasks.Count != 0 && waitForCompletion)
            {
                Log.Information("等待 {Count} 个后台服务停止任务完成...", tasks.Count);
                try
                {
                    // 等待异步服务停止完成，设置超时
                    if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(15))) // 增加超时时间到15秒
                        Log.Warning("一个或多个后台服务未在15秒超时时间内正常停止。");
                    else
                        Log.Information("所有后台服务已成功停止 (异步部分)。");
                }
                catch (AggregateException aex)
                {
                    // 检查是否所有内部异常都是任务取消异常
                    var allCanceled =
                        aex.InnerExceptions.All(ex => ex is TaskCanceledException or OperationCanceledException);

                    if (allCanceled)
                    {
                        Log.Information("后台服务停止时任务被正常取消，这是应用程序关闭时的预期行为");
                    }
                    else
                    {
                        // 记录非取消类型的异常
                        LogUnhandledException(aex, "Background Services Stop Aggregate Error");
                        foreach (var innerEx in aex.InnerExceptions.Where(ex =>
                                     ex is not TaskCanceledException and not OperationCanceledException))
                            LogUnhandledException(innerEx, "Background Services Stop Inner Error");
                    }
                }
                catch (Exception ex)
                {
                    LogUnhandledException(ex, "Background Services Stop Wait Error");
                }
            }
            else if (!waitForCompletion)
            {
                Log.Information("后台服务停止已发起 (不等待完成)。");
            }
            else
            {
                Log.Information("没有后台服务需要停止。");
            }
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex, "Background Services Stop Error");
        }

        Log.Information("=== StopBackgroundServices 方法执行完毕。 ===");
    }
}