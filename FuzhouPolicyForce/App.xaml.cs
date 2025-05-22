using System.IO;
using System.Windows;
using System.Windows.Threading;
using Common.Services.License;
using Common.Services.Ui;
using FuzhouPolicyForce.ViewModels;
using FuzhouPolicyForce.Views;
using FuzhouPolicyForce.Views.Settings;
using Serilog;
using SharedUI.ViewModels.Settings;
using Timer = System.Timers.Timer;
using System.Diagnostics;
using FuzhouPolicyForce.ViewModels.Settings;
using FuzhouPolicyForce.WangDianTong;
using SharedUI.Views.Settings;
using System.Globalization;
using System.Net.Http;
using SharedUI.Views.Windows;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;
using History; // 新增引用
using Camera; // 注册相机模块需要
using BalanceSorting.Modules;
using BalanceSorting.Service;
using Camera.Interface;
using Common; // 注册多摆轮模块需要

namespace FuzhouPolicyForce;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\FuzhouPolicyForce_App_Mutex";
    private Timer? _cleanupTimer;
    private bool _ownsMutex;
    
    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        containerRegistry.RegisterSingleton<ILicenseService, LicenseService>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<WangDianTongSettingsView, WangDianTongSettingsViewModel>();
        containerRegistry.RegisterForNavigation<ShenTongLanShouSettingsView, ShenTongLanShouSettingsViewModel>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterSingleton<IWangDianTongApiService, WangDianTongApiService>();

        // 注册旺店通API服务 V2
        // 注册 HttpClient
        containerRegistry.RegisterSingleton<HttpClient>();
        // 注册新的旺店通API服务实现
        containerRegistry.RegisterSingleton<IWangDianTongApiServiceV2, WangDianTongApiServiceImplV2>();
    }

    protected override Window CreateShell()
    {
        // 检查是否已经运行（进程级检查）
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Current.Shutdown();
            return null!;
        }
        try
        {
            // 尝试创建全局Mutex
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew)
            {
                // 强制立即运行所有模块，防止注入顺序问题
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run();
                return Container.Resolve<MainWindow>();
            }

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return null!;
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
            MessageBox.Show($"启动程序时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            LocalizeDictionary.Instance.DefaultProvider = ResxProvider;
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

            // 注册全局异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // 启动DUMP文件清理任务
            StartCleanupTask();

            Log.Information("应用程序启动");
            base.OnStartup(e);

            // 验证授权
            if (!CheckLicense())
            {
                // 授权验证失败，退出应用
                Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序启动时发生错误");
            MessageBox.Show($"应用程序启动时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    /// <summary>
    ///     验证授权
    /// </summary>
    /// <returns>验证是否通过</returns>
    private bool CheckLicense()
    {
        try
        {
            var licenseService = Container.Resolve<ILicenseService>();
            var notificationService = Container.Resolve<INotificationService>();

            var (isValid, message) = licenseService.ValidateLicenseAsync().Result;

            if (!isValid)
            {
                Log.Warning("授权验证失败: {Message}", message);
                MessageBox.Show(message ?? "软件授权验证失败，请联系厂家获取授权。", "授权验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 获取授权过期时间并计算剩余天数
            var expirationDate = licenseService.GetExpirationDateAsync().Result;
            var daysLeft = (expirationDate - DateTime.Now).TotalDays;
            Log.Information("授权剩余天数: {DaysLeft} 天", Math.Ceiling(daysLeft));

            if (!string.IsNullOrEmpty(message))
            {
                // 有效但有警告消息（如即将过期）
                Log.Warning("授权警告: {Message}", message);
                MessageBox.Show(message, "授权提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
                notificationService.ShowWarning(message);
            }
            else
            {
                Log.Information("授权验证通过");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "授权验证过程发生错误");
            MessageBox.Show("授权验证过程发生错误，请联系厂家获取支持。", "授权验证", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");

            // 停止清理定时器
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Stop();
                _cleanupTimer.Dispose();
                Log.Information("清理定时器已停止");
            }

            // 手动停止华睿相机服务和多摆轮分拣服务
            StopHuaRayCameraService();
            StopMultiPendulumSortService();

            // 等待所有日志写入完成
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

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<CommonServicesModule>(); // 注册公共服务模块
        moduleCatalog.AddModule<HistoryModule>(); // 注册历史模块
        moduleCatalog.AddModule<HuaRayCameraModule>(); // 注册华睿相机模块
        moduleCatalog.AddModule<MultiPendulumSortModule>(); // 注册多摆轮分拣模块
    }

    /// <summary>
    /// 手动停止华睿相机服务
    /// </summary>
    public static void StopHuaRayCameraService()
    {
        try
        {
            // 通过 Prism 容器解析并停止华睿相机服务
            var container = ContainerLocator.Container;
            var huaRayService = container.Resolve<ICameraService>();
            huaRayService.Stop();
            Log.Information("华睿相机服务已手动停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "手动停止华睿相机服务时发生异常");
        }
    }

    /// <summary>
    /// 手动停止多摆轮分拣服务
    /// </summary>
    private static void StopMultiPendulumSortService()
    {
        try
        {
            // 通过 Prism 容器解析多摆轮分拣服务
            var container = ContainerLocator.Container;
            var sortService = container.Resolve<IPendulumSortService>();
            sortService.StopAsync();
            // 判断是否实现 IDisposable，优先调用 Dispose
            if (sortService is IDisposable disposable)
            {
                disposable.Dispose();
                Log.Information("多摆轮分拣服务已手动 Dispose");
            }
            else
            {
                Log.Information("多摆轮分拣服务未实现 Dispose，仅释放引用");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "手动停止多摆轮分拣服务时发生异常");
        }
    }
}