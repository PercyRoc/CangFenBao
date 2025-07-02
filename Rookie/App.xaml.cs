using System.Globalization;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Rookie.ViewModels.Settings;
using Rookie.ViewModels.Windows;
using Rookie.ViewModels.Windows.Dialogs;
using Rookie.Views.Dialogs;
using Rookie.Views.Settings;
using Rookie.Views.Windows;
using Serilog;
using SharedUI.Extensions;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;

namespace Rookie;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = @"Global\Rookie_CangFenBao_App_Mutex";
    private static Mutex? _mutex;
    private bool _ownsMutex;

    // Static provider instance for XAML binding
    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;


    protected override Window CreateShell()
    {
        // Remove logger configuration from here
        // ConfigureLoggerFromSettings(); 
        Log.Information("尝试创建 Shell");

        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew || _mutex.WaitOne(TimeSpan.Zero, false))
            {
                _ownsMutex = true;
                Log.Information("获取到 Mutex. 正在创建 MainWindow.");
                return Container.Resolve<MainWindow>();
            }
            Log.Warning("应用程序已在运行. 正在关闭.");
            MessageBox.Show("Application is already running!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return null!;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Mutex 检查或 MainWindow 创建期间出错.");
            MessageBox.Show($"Fatal error during startup: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();
        containerRegistry.RegisterInstance<IConfiguration>(configuration);

        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();
        containerRegistry.RegisterSingleton<IHostedService, CameraStartupService>();
        containerRegistry.RegisterSingleton<PackageTransferService>();

        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>();
        containerRegistry.RegisterForNavigation<RookieApiSettingsView, RookieApiSettingsViewModel>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureLoggerFromSettings();
        Log.Information("Logger configured in OnStartup.");

        try
        {
            // Set the static instance as the default provider (optional but good practice)
            LocalizeDictionary.Instance.DefaultProvider = ResxProvider;
            // Force English culture for testing
            var culture = new CultureInfo("zh-CN");
            LocalizeDictionary.Instance.Culture = culture;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error setting culture settings"); // Simplified message
        }

        Log.Information("应用程序启动 (OnStartup)");
        RegisterGlobalExceptionHandling();
        base.OnStartup(e);

        _ = Task.Run(InitializeServicesAsync);
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            Log.Information("开始初始化后台服务...");
            var cameraStartupService = Container.Resolve<IHostedService>();
            await cameraStartupService.StartAsync(CancellationToken.None);
            Log.Information("相机服务已通过 CameraStartupService 成功启动.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "服务初始化期间发生严重错误. 应用程序将关闭.");
            Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"Failed to initialize background services. The application will close.\n\nError: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown(1);
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序退出 (OnExit). 退出代码: {ExitCode}", e.ApplicationExitCode);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var shutdownTask = ShutdownServicesAsync(cts.Token);
            shutdownTask.Wait(cts.Token);
            Log.Information("服务关闭在超时时间内完成.");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("服务关闭因超时而被取消.");
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
            Log.CloseAndFlush();
            ReleaseMutex();
            base.OnExit(e);
        }
    }

    private async Task ShutdownServicesAsync(CancellationToken cancellationToken)
    {
        Log.Information("开始关闭后台服务...");
        List<Task> shutdownTasks = [];
        try
        {
            var cameraStartupService = Container.Resolve<IHostedService>();
            shutdownTasks.Add(cameraStartupService.StopAsync(cancellationToken));
            if (shutdownTasks.Count != 0)
            {
                Log.Information("等待 {Count} 个服务停止...", shutdownTasks.Count);
                await Task.WhenAll(shutdownTasks);
                Log.Information("所有等待的后台服务已停止.");
            }
            else { Log.Information("未找到需要等待的可停止后台服务."); }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在 WhenAll 期间停止后台服务出错.");
        }
    }

    private static void ConfigureLoggerFromSettings()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", false, true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
            // Log a message immediately after configuration to confirm it works
            Log.Information("已从 appsettings.json 配置 Serilog (called from ConfigureLoggerFromSettings)");
        }
        catch (Exception ex)
        {
            // Create a fallback logger INSTANTLY if configuration fails
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug() // Log Debug and higher for fallback
                .WriteTo.Console()
                .WriteTo.Debug()
                .CreateLogger();
            // Log the configuration error using the fallback logger
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
            Log.Fatal(args.ExceptionObject as Exception, "捕获到未处理的 AppDomain 异常. IsTerminating: {IsTerminating}", args.IsTerminating);
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
                catch (ApplicationException ex) { Log.Warning(ex, "释放 Mutex 失败 (已释放?)"); }
                catch (Exception ex) { Log.Error(ex, "释放 Mutex 出错"); }
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