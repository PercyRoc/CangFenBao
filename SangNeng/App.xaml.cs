using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Camera;
using Camera.Interface;
using Camera.Services.Implementations.RenJia;
using Common;
using Serilog;
using Sunnen.Services;
using Sunnen.ViewModels.Dialogs;
using Sunnen.ViewModels.Settings;
using Sunnen.ViewModels.Windows;
using Sunnen.Views.Dialogs;
using Sunnen.Views.Settings;
using Sunnen.Views.Windows;
using Weight;
using Weight.Services;
using WPFLocalizeExtension.Engine;
using Window = System.Windows.Window;
using History;
using SharedUI.Views.Windows;

namespace Sunnen;

/// <summary>
///     应用程序入口
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\SangNeng_App_Mutex";
    private bool _ownsMutex;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行（进程级检查）
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("Application is already running. Please do not start it again!", "Information", MessageBoxButton.OK,
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
                // 确保模块已完全初始化
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run();
                return Container.Resolve<MainWindow>();
            }

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("Application is already running. Please do not start it again!", "Information", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0);
                return null!;
            }

            {
                // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
                _ownsMutex = true;
                // 确保模块已完全初始化
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run();
                return Container.Resolve<MainWindow>();
            }
        }
        catch (Exception ex)
        {
            // Mutex创建或获取失败
            Log.Error(ex, "检查应用程序实例时发生错误");
            MessageBox.Show($"Error starting application: {ex.Message}", "Error", MessageBoxButton.OK,
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

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 确保 Serilog.Log.Logger 已经被配置 (通常在 OnStartup 早期)
        // 如果 Log.Logger 此时可能可能未初始化，需要调整 App.xaml.cs 的初始化顺序
        // 或者在这里进行一个简单的检查和配置。
        // 但基于现有代码，OnStartup 中配置 Logger，RegisterTypes 在那之后被框架调用以构建Shell。
        containerRegistry.RegisterInstance(Log.Logger);

        // 使用扩展方法注册设备服务 (现在它们依赖的 Startup 服务已明确为 Singleton)
        containerRegistry.RegisterSingleton<ISangNengService, SangNengService>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();
        // 注册窗口和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        
        containerRegistry.RegisterForNavigation<PalletSettingsView, PalletSettingsViewModel>();
        containerRegistry.RegisterDialog<SettingsControl, SettingsDialogViewModel>();
        // 注册桑能设置页面
        containerRegistry.RegisterForNavigation<SangNengSettingsPage, SangNengSettingsViewModel>();
    }

    /// <summary>
    /// 配置模块目录
    /// </summary>
    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<CommonServicesModule>();
        moduleCatalog.AddModule<RenJiaIndustrialCameraModule>();
        moduleCatalog.AddModule<WeightModule>();
        moduleCatalog.AddModule<HistoryModule>();
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30,
                encoding: System.Text.Encoding.UTF8)
            .CreateLogger();

        Log.Information("应用程序启动");
        // 设置 WPFLocalizeExtension 的区域性
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.Culture = new CultureInfo("en-US");
        // 先调用基类方法初始化容器
        base.OnStartup(e);
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序正在关闭，开始停止服务...");

            // 使用 TryDisposeService 关闭服务
            TryDisposeService<ICameraService>("默认相机服务 (ICameraService)");
            TryDisposeService<RenJiaCameraService>("人加体积相机服务 (RenJiaCameraService)");
            TryDisposeService<IWeightService>("重量服务 (IWeightService)");

            // 等待所有日志写入完成
            Log.Information("应用程序关闭流程完成。");
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush(); // 确保即使在异常情况下也尝试刷新日志
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

    /// <summary>
    /// 尝试解析并处置一个服务。
    /// </summary>
    /// <typeparam name="TService">要处置的服务类型。</typeparam>
    /// <param name="serviceName">用于日志记录的服务名称。</param>
    private void TryDisposeService<TService>(string serviceName) where TService : class, IDisposable
    {
        try
        {
            // 假设服务已在容器中注册为 TService 类型。
            // 如果服务是可选的或条件注册的，可能需要 Container.IsRegistered<TService>() 检查。
            var service = Container.Resolve<TService>();
            
            Log.Information("正在处置服务: {ServiceName}...", serviceName);
            service.Dispose(); // 调用 Dispose 方法进行清理
            Log.Information("服务 {ServiceName} 的处置操作已发起或完成。", serviceName);
        }
        // 根据DI容器的具体行为，可能需要捕获更具体的异常，如 Prism 的 ResolutionFailedException。
        catch (Exception ex) 
        {
            // 捕获解析服务或调用 Dispose 时可能发生的任何错误。
            Log.Warning(ex, "解析或处置服务 {ServiceName} 时发生错误。该服务可能未注册、已被处置或在处置过程中出错。", serviceName);
        }
    }
}