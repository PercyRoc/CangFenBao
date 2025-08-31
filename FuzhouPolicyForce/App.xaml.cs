using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using Common.Extensions;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.License;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using FuzhouPolicyForce.Services.AnttoWeight;
using FuzhouPolicyForce.ViewModels;
using FuzhouPolicyForce.ViewModels.Settings;
using FuzhouPolicyForce.Views;
using FuzhouPolicyForce.Views.Settings;
using FuzhouPolicyForce.WangDianTong;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Dialogs;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Dialogs;
using SharedUI.Views.Settings;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;
using Timer = System.Timers.Timer;

namespace FuzhouPolicyForce;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private const string MutexName = "Global\\FuzhouPolicyForce_App_Mutex";
    private static Mutex? _mutex;
    private Timer? _cleanupTimer;
    private bool _ownsMutex;

    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册授权服务
        containerRegistry.RegisterSingleton<ILicenseService, LicenseService>();

        // 注册设置页面的ViewModel
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<WangDianTongSettingsView, WangDianTongSettingsViewModel>();
        containerRegistry.RegisterForNavigation<AnttoWeightSettingsView, AnttoWeightSettingsViewModel>();

        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
        containerRegistry.RegisterDialog<CalibrationDialogView, CalibrationDialogViewModel>();

        // 注册多摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(PendulumServiceType.Multi);
        containerRegistry.RegisterSingleton<PendulumSortService>();

        // 注册旺店通API服务 V1
        // 注册 HttpClient
        containerRegistry.RegisterSingleton<HttpClient>();
        containerRegistry.RegisterSingleton<IWangDianTongApiService, WangDianTongApiService>();

        // 注册安通称重API服务
        containerRegistry.RegisterSingleton<IAnttoWeightService, AnttoWeightService>();
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

            if (createdNew) return Container.Resolve<MainWindow>();

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return null!;
            }

            // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
            _ownsMutex = true;
            return Container.Resolve<MainWindow>();
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
    ///     检查是否已有相同名称的应用程序实例在运行
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

            // 初始化全局异常处理器
            GlobalExceptionHandler.Initialize();

            // 启动DUMP文件清理任务
            StartCleanupTask();

            Log.Information("应用程序启动");
            base.OnStartup(e);

            // 验证授权
            if (!CheckLicense())
            {
                // 授权验证失败，退出应用
                Current.Shutdown();
                return;
            }

            // 启动服务
            _ = InitializeServicesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序启动时发生错误");
            MessageBox.Show($"应用程序启动时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机托管服务启动成功");

            // 启动摆轮分拣服务
            var pendulumService = Container.Resolve<PendulumSortService>();
            await pendulumService.StartAsync();
            Log.Information("摆轮分拣服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            MessageBox.Show($"启动服务时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    var fileName = Path.GetFileName(file);

                    // 跳过以"crash"开头的崩溃转储文件
                    if (fileName.StartsWith("crash_", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Debug("保留崩溃转储文件: {FileName}", fileName);
                        continue;
                    }

                    File.Delete(file);
                    deletedCount++;
                    Log.Debug("删除DUMP文件: {FileName}", fileName);
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

            // 停止服务
            _ = ShutdownServicesAsync();
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

    private async Task ShutdownServicesAsync()
    {
        try
        {
            // 停止摆轮分拣服务
            var pendulumService = Container.Resolve<PendulumSortService>();
            await pendulumService.StopAsync();
            Log.Information("摆轮分拣服务已停止");

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
            Log.Error(ex, "关闭服务时发生错误");
            await Log.CloseAndFlushAsync();
        }
    }
}