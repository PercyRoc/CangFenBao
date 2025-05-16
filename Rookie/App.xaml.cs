using System.Windows;
using Common.Extensions;
using Microsoft.Extensions.Configuration;
using Rookie.ViewModels.Windows;
using Rookie.ViewModels.Windows.Dialogs;
using Rookie.Views.Dialogs;
using Rookie.Views.Windows;
using Serilog;
using Rookie.Services;
using Rookie.ViewModels.Settings;
using Rookie.Views.Settings;
using SharedUI.Views.Settings;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Windows;
using SharedUI.Views.Dialogs;
using System.Globalization;
using WPFLocalizeExtension.Engine;
using Camera;
using Camera.Interface;
using Sorting_Car;
using Weight;
using Weight.Services;
using Camera.Services.Implementations.Hikvision.Volume;
using Camera.Services.Implementations.Hikvision.Security;

namespace Rookie;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = @"Global\Rookie_CangFenBao_App_Mutex";
    private bool _ownsMutex;
    protected override Window CreateShell()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew || _mutex.WaitOne(TimeSpan.Zero, false))
            {
                _ownsMutex = true;

                // 确保模块已完全初始化, 这解决了之前的服务解析时序问题
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run(); 
                Log.Information("模块已通过 moduleManager.Run() 显式初始化于 CreateShell 开头。");
                
                // 现在可以安全地解析主窗口及其依赖
                return Container.Resolve<MainWindow>();
            }

            MessageBox.Show("Application is already running!", "Information", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
            return null!;
        }
        catch (Exception ex)
        {
            // 保留这个顶层 catch 以处理 CreateShell 期间的意外故障
            Log.Fatal(ex, "创建 Shell 期间发生致命错误。");
            MessageBox.Show($"Fatal error during startup: {ex.Message}", "Startup Error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(1);
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        containerRegistry.RegisterInstance<IConfiguration>(configuration);

        containerRegistry.AddCommonServices();
        containerRegistry.RegisterSingleton<BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterSingleton<HistoryDialogViewModel>();


        containerRegistry.RegisterSingleton<IRookieApiService, RookieApiService>();
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();
        containerRegistry.RegisterForNavigation<RookieApiSettingsView, RookieApiSettingsViewModel>();
        
        containerRegistry.RegisterForNavigation<SerialPortSettingsView, SerialPortSettingsViewModel>();
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<FullFeaturedCameraModule>();
        // moduleCatalog.AddModule<IntegratedCameraModule>();
        moduleCatalog.AddModule<WeightModule>();
        moduleCatalog.AddModule<SortingCarModule>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var progressWindow =
            // 显示启动进度窗口
            new ProgressIndicatorWindow("Initializing services, please wait...");
        progressWindow.Show();

        ConfigureLoggerFromSettings();
        Log.Information("Logger configured in OnStartup.");

        Log.Information("应用程序启动 (OnStartup)");
        RegisterGlobalExceptionHandling();
        base.OnStartup(e);

        // 设置 WPFLocalizeExtension 的区域性
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.Culture = new CultureInfo("en-US");
        progressWindow.Dispatcher.Invoke(progressWindow.Close);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序退出 (OnExit). 退出代码: {ExitCode}", e.ApplicationExitCode);

        ProgressIndicatorWindow? progressWindow = null;
        try
        {
            progressWindow = new ProgressIndicatorWindow("Shutting down services, please wait...");
            progressWindow.Show();

            // 服务关闭逻辑
            Log.Information("开始关闭应用程序服务...");

            TryDisposeService<ICameraService>("HikvisionIndustrialCameraService");
            TryDisposeService<HikvisionVolumeCameraService>("HikvisionVolumeCameraService");
            TryDisposeService<HikvisionSecurityCameraService>("HikvisionSecurityCameraService");
            TryDisposeService<IWeightService>("WeightService");
            
            Log.Information("应用程序服务关闭完成或已发起。");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("服务关闭因超时而被取消 (OperationCanceledException).");
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Any(ex => ex is OperationCanceledException))
        {
            Log.Warning("服务关闭因超时而被取消 (AggregateException).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待服务关闭期间出错.");
        }
        finally
        {
            progressWindow?.Dispatcher.Invoke(() => progressWindow.Close());
            await Log.CloseAndFlushAsync();
            ReleaseMutex();
            base.OnExit(e);
        }
    }

    private static void ConfigureLoggerFromSettings()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
            Log.Information("已从 appsettings.json 配置 Serilog (called from ConfigureLoggerFromSettings)");
        }
        catch (Exception ex)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.Debug()
                .CreateLogger();
            Log.Error(ex, "从 appsettings.json 配置 Serilog 失败. 使用基本备用日志记录器.");
        }
    }

    private void RegisterGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的调度程序异常");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的任务异常");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "捕获到未处理的 AppDomain 异常. IsTerminating: {IsTerminating}",
                args.IsTerminating);
        };
        Log.Information("全局异常处理程序已注册");
    }

    private void TryDisposeService<TService>(string serviceName) where TService : class, IDisposable
    {
        try
        {
            // 尝试解析服务。对于可选服务或根据条件注册的服务，这可能需要更复杂的逻辑
            // (例如 TryResolve 或检查服务是否实际已激活)。
            // 此处假设如果服务已注册，则应尝试处置。
            var service = Container.Resolve<TService>();
            
            // Resolve<T> 在 Prism 中如果服务未注册通常会抛出异常。
            // 如果服务可能未注册，应使用 Container.IsRegistered<TService>() 检查或 TryResolve。
            // 为简化，此处假定如果我们要关闭它，它应该已被注册。
            // 注意: Prism 的 Resolve<T>() 在某些配置下，如果找不到，可能返回 null，而不是抛出。
            // 然而，更常见的行为是抛出 ResolutionFailedException。下面的 null 检查是为了以防万一。

            Log.Information("正在处置服务: {ServiceName}...", serviceName);
            service.Dispose();
            Log.Information("服务 {ServiceName} 的处置操作已发起或完成。", serviceName);
        }
        catch (Exception ex) // 更具体的异常类型，如 ResolutionFailedException (取决于DI容器) 可能更合适
        {
            // 捕获解析服务或调用 Dispose 时可能发生的任何错误。
            Log.Warning(ex, "解析或处置服务 {ServiceName} 时发生错误。该服务可能未注册、已被处置或在处置过程中出错。", serviceName);
        }
    }

    private void ReleaseMutex()
    {
        try
        {
            if (_mutex == null) return;
            if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
            {
                try
                {
                    _mutex.ReleaseMutex();
                    Log.Information("Mutex 已释放");
                }
                catch (ApplicationException ex)
                {
                    Log.Warning(ex, "释放 Mutex 失败 (已释放?)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 Mutex 出错");
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
            Log.Debug("Mutex 已处置");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在 Mutex 释放/处置期间出错");
        }
    }
}