using System.Globalization;
using System.Net.Http;
using System.Windows;
using AmericanQuickHands.ViewModels;
using AmericanQuickHands.Views;
using AmericanQuickHands.Models.Api;
using AmericanQuickHands.ViewModels.Settings;
using AmericanQuickHands.Views.Settings;
using Camera;
using Camera.Interface;
using Common;
using History;
using History.Data;
using Microsoft.Extensions.Configuration;
using Serilog;
using WPFLocalizeExtension.Engine;

namespace AmericanQuickHands;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = @"Global\AmericanQuickHands_CangFenBao_App_Mutex";
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
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run();
                Log.Information("模块已通过 moduleManager.Run() 显式初始化于 CreateShell 内部或之前。");
                var shellWindow = Container.Resolve<MainWindow>();
                return shellWindow;
            }

            MessageBox.Show("Application is already running!", "Information", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
            return null!;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "创建 Shell 期间发生致命错误。");
            MessageBox.Show($"Fatal error during startup: {ex.Message}", "Startup Error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(1);
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterInstance(Log.Logger); // Register the static Log.Logger instance
        Log.Information("Serilog.ILogger instance registered in DI container.");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true) // 设为 optional: true，因为新项目可能还没有这个文件
            .Build();
        containerRegistry.RegisterInstance<IConfiguration>(configuration);
        // 注册 HttpClient 用于 API 调用
        containerRegistry.RegisterSingleton<HttpClient>();
        
        // 注册美国快手API服务
        containerRegistry.RegisterSingleton<IAmericanQuickHandsApiService, AmericanQuickHandsApiService>();
        
        // 注册视图和视图模型
        containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<AmericanQuickHandsApiSettingsView, AmericanQuickHandsApiSettingsViewModel>();
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<CommonServicesModule>();
        moduleCatalog.AddModule<HuaRayCameraModule>(); // 包含华睿相机支持
        moduleCatalog.AddModule<HistoryModule>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureLoggerFromSettings();
        Log.Information("应用程序启动 (OnStartup)");
        RegisterGlobalExceptionHandling();
        base.OnStartup(e);
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.Culture = new CultureInfo("en-US");
    }

    protected override async void OnInitialized()
    {
        base.OnInitialized();

        try
        {
            var historyService = Container.Resolve<IPackageHistoryDataService>();
            await historyService.InitializeAsync();
            Log.Information("IPackageHistoryDataService 初始化成功。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize IPackageHistoryDataService in OnInitialized.");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序退出 (OnExit). 退出代码: {ExitCode}", e.ApplicationExitCode);
        try
        {
            Log.Information("开始关闭应用程序服务...");
            if (Container.IsRegistered<ICameraService>())
            {
                try
                {
                    var cameraService = Container.Resolve<ICameraService>();
                    cameraService.Stop();
                    Log.Information("ICameraService.Stop() 已调用。");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "调用 ICameraService.Stop() 时发生错误。");
                }
            }
            Log.Information("应用程序服务关闭完成或已发起。");

            // 等待日志刷新完成，设置一个合理的超时时间
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
            await Log.CloseAndFlushAsync(); // 确保日志在退出前刷新
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
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            // 检查配置中是否有 Serilog 配置节
            if (configuration.GetSection("Serilog").Exists())
            {
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();
                Log.Information("已从 appsettings.json 配置 Serilog (called from ConfigureLoggerFromSettings)");
            }
            else
            {
                // 提供一个基本的备用日志配置
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console()
                    .WriteTo.File("logs/LosAngelesExpress-.txt", rollingInterval: RollingInterval.Day)
                    .CreateLogger();
                Log.Information("appsettings.json 中未找到 Serilog 配置或文件不存在。已使用基本备用日志记录器。");
            }
        }
        catch (Exception ex)
        {
            // 如果从文件配置失败，也使用基本备用日志记录器
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs/fallback-LosAngelesExpress-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
            Log.Error(ex, "从 appsettings.json 配置 Serilog 失败. 已使用备用日志记录器.");
        }
    }

    private void RegisterGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的调度程序异常");
            args.Handled = true; // 通常在顶层处理后标记为已处理
            // 考虑显示一个错误消息给用户
            MessageBox.Show($"An unhandled error occurred: {args.Exception.Message}", "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的任务异常");
            args.SetObserved();
            // 考虑记录或通知，因为这通常发生在后台线程
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "捕获到未处理的 AppDomain 异常. IsTerminating: {IsTerminating}", args.IsTerminating);
            // 如果应用程序正在终止，可能无法显示UI消息
            if (!args.IsTerminating)
            {
                // 尝试显示错误消息
                MessageBox.Show($"A critical unhandled error occurred: {ex?.Message}. The application might close.", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        Log.Information("全局异常处理程序已注册");
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
                catch (ApplicationException ex) // Mutex不属于调用线程
                {
                    Log.Warning(ex, "释放 Mutex 失败 (ApplicationException - 可能已被其他实例释放或当前线程不拥有它)");
                }
                catch (ObjectDisposedException ex) // Mutex已被处置
                {
                    Log.Warning(ex, "释放 Mutex 失败 (ObjectDisposedException - Mutex已被处置)");
                }
                catch (Exception ex) // 其他异常
                {
                    Log.Error(ex, "释放 Mutex 出错 (一般性异常)");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在检查 Mutex 状态或准备释放期间出错。");
        }
        finally // 确保 _mutex 被处置
        {
            if (_mutex != null)
            {
                _mutex.Dispose();
                _mutex = null;
                _ownsMutex = false;
                Log.Debug("Mutex 已处置");
            }
        }
    }
}