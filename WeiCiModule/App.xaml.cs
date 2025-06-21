using System.Globalization;
using System.Windows;
using WeiCiModule.ViewModels;
using WeiCiModule.Views;
using Serilog;
using Common.Services.Settings;
using Microsoft.Extensions.Configuration;
using System.IO;
using Camera.Services.Implementations.TCP;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;
using WeiCiModule.Services;
using WeiCiModule.ViewModels.Settings;
using WeiCiModule.Views.Settings;
using Common;
using History;
using System.Runtime.InteropServices;

namespace WeiCiModule;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\WeiCiModule_App_Mutex_B3A7F8D1-C2E9-4B5A-9A1D-F8C7E0A1B2C3";
    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;
    
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        try
        {
            LocalizeDictionary.Instance.DefaultProvider = ResxProvider;
            var culture = new CultureInfo("en-US");
            LocalizeDictionary.Instance.Culture = culture;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error setting culture settings");
        }

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        Log.Information("Serilog 已从 appsettings.json 成功初始化");

        Current.DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                if (args?.Exception != null)
                {
                    Log.Fatal(args.Exception, "应用程序发生未经处理的致命异常!");
                }
                else
                {
                    Log.Fatal("应用程序发生未经处理的致命异常，但异常对象为空!");
                }
                
                if (args != null)
                {
                    args.Handled = true;
                }
                
                MessageBox.Show($"发生了一个致命错误，应用程序即将关闭。详情请查看日志。", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown(-1);
            }
            catch (Exception ex)
            {
                // 如果异常处理程序本身出错，至少尝试记录
                try
                {
                    Log.Error(ex, "异常处理程序内部发生错误");
                }
                catch
                {
                    // 忽略日志错误
                }
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                Log.Error(args.Exception, "后台任务发生未观察到的异常!");
                args.SetObserved();
            }
            catch (Exception ex)
            {
                // 忽略异常处理程序内部的错误
                try { Log.Error(ex, "UnobservedTaskException处理程序内部错误"); }
                catch
                {
                    // ignored
                }
            }
        };
        
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception exception)
                {
                    Log.Fatal(exception, "AppDomain 级别捕获到未经处理的致命异常! IsTerminating: {IsTerminating}", args.IsTerminating);
                }
                else
                {
                    Log.Fatal("AppDomain 级别捕获到未经处理的致命异常，但异常对象无法转换! IsTerminating: {IsTerminating}", args.IsTerminating);
                }
            }
            catch (Exception ex)
            {
                // 忽略异常处理程序内部的错误
                try { Log.Error(ex, "UnhandledException处理程序内部错误"); }
                catch
                {
                    // ignored
                }
            }
        };

        base.OnStartup(e);
        Log.Information("应用程序 OnStartup 完成。");
    }

    protected override Window CreateShell()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew)
        {
            // 确保模块已完全初始化, 这解决了之前的服务解析时序问题
            var moduleManager = Container.Resolve<IModuleManager>();
            moduleManager.Run(); 
            // 这是第一个实例，正常启动
            return Container.Resolve<MainWindow>();
        }

        // 已有实例在运行
        MessageBox.Show("WeiCiModule is already running.", "Application Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
        Current.Shutdown();
        return null!; // 确保在所有路径上都有返回值
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        // 注册基础模块
        moduleCatalog.AddModule<CommonServicesModule>();
        moduleCatalog.AddModule<HistoryModule>();
        Log.Information("已注册 CommonServicesModule, HistoryModule 和 WeiCiModule 模块");
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<TcpCameraService>();
        containerRegistry.RegisterSingleton<MainViewModel>();
        containerRegistry.RegisterSingleton<SettingsDialogViewModel>();
        containerRegistry.RegisterSingleton<ChuteSettingsViewModel>();
        containerRegistry.RegisterSingleton<ModulesTcpSettingsViewModel>();
        containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<ChuteSettingsView, ChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<ModulesTcpSettingsView, ModulesTcpSettingsViewModel>();
        containerRegistry.RegisterSingleton<IModuleConnectionService,ModuleConnectionService>();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Log.Information("应用程序初始化完成，准备启动后台服务...");
        
        _ = Task.Run(async () =>
        {
            // 启动 ModuleConnectionService
            var moduleConnectionService = Container.Resolve<IModuleConnectionService>();
            var settingsService = Container.Resolve<ISettingsService>();
            try
            {
                var tcpSettings = settingsService.LoadSettings<Models.Settings.ModelsTcpSettings>();
                if (!string.IsNullOrEmpty(tcpSettings.Address))
                {
                    Log.Information("正在启动 ModuleConnectionService，监听地址: {IpAddress}:{Port}...", tcpSettings.Address, tcpSettings.Port);
                    var started = await moduleConnectionService.StartServerAsync(tcpSettings.Address, tcpSettings.Port);
                    if (started)
                    {
                        Log.Information("ModuleConnectionService 已成功启动。");
                    }
                    else
                    {
                        Log.Error("ModuleConnectionService 启动失败。");
                    }
                }
                else
                {
                    Log.Error("无法加载 ModelsTcpSettings 或IP地址无效，ModuleConnectionService 未启动。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动 ModuleConnectionService 时发生错误。");
            }

            // 启动 TcpCameraService
            var tcpCameraService = Container.Resolve<TcpCameraService>();
            try
            {
                Log.Information("正在启动 TcpCameraService...");
                if (tcpCameraService.Start())
                {
                    Log.Information("TcpCameraService 已成功请求启动。连接状态将通过事件回调更新。");
                }
                else
                {
                    Log.Error("TcpCameraService 启动请求失败。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动 TcpCameraService 时发生错误。");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序正在准备退出...");
        var tasks = new List<Task>();

        // 首先释放MainViewModel资源（包含专用线程）
        try
        {
            if (ContainerLocator.Container.IsRegistered<MainViewModel>())
            {
                var mainViewModel = ContainerLocator.Container.Resolve<MainViewModel>();
                Log.Information("正在释放 MainViewModel 资源...");
                mainViewModel.Dispose();
                Log.Information("MainViewModel 资源已释放。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放 MainViewModel 时发生错误");
        }

        if (ContainerLocator.Container.IsRegistered<IModuleConnectionService>())
        {
            var moduleConnectionService = ContainerLocator.Container.Resolve<IModuleConnectionService>();
            Log.Information("正在尝试释放 ModuleConnectionService...");
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    if (moduleConnectionService is IDisposable disposableService)
                    {
                        disposableService.Dispose();
                        Log.Information("ModuleConnectionService 已成功释放。");
                    }
                    else
                    {
                        // 回退到原来的停止方法
                        moduleConnectionService.StopServerAsync().Wait(TimeSpan.FromSeconds(3));
                        Log.Information("ModuleConnectionService 已成功停止。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 ModuleConnectionService 时发生意外错误。");
                }
            }));
        }

        // Stop TcpCameraService
        if (ContainerLocator.Container.IsRegistered<TcpCameraService>())
        {
            var tcpCameraService = ContainerLocator.Container.Resolve<TcpCameraService>();
            Log.Information("正在尝试停止 TcpCameraService...");
            tasks.Add(Task.Run(() => 
            {
                try
                {
                    tcpCameraService.Dispose(); // 使用Dispose而不是Stop，确保完全清理
                    Log.Information("TcpCameraService 已成功释放资源。");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 TcpCameraService 时发生意外错误。");
                }
            }));
        } else {
            Log.Warning("TcpCameraService 未注册，跳过停止操作。");
        }

        if (tasks.Count != 0)
        {
            Log.Information("等待后台服务停止...");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // 减少到3秒超时
                var allTasks = Task.WhenAll(tasks);
                
                if (allTasks.Wait(TimeSpan.FromSeconds(3)))
                {
                    Log.Information("所有请求停止的后台服务均已处理完毕。");
                }
                else
                {
                    Log.Warning("一个或多个后台服务未在3秒超时内完全停止，继续退出流程。");
                }
            }
            catch (AggregateException ae) when (ae.InnerExceptions.Any(ex => ex is TimeoutException))
            {
                Log.Warning("停止一个或多个后台服务时发生超时。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "等待后台服务停止时发生意外错误。");
            }
        }

        // 强制垃圾回收，确保所有资源被释放
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _mutex?.ReleaseMutex();
        if (_mutex != null)
        {
            Log.Information("Mutex 即将释放。");
            _mutex.Dispose();
            _mutex = null;
            Log.Information("Mutex 已成功释放并置为null。");
        }
        
        base.OnExit(e);
        Log.Information("应用程序已退出。 Status Code: {ExitCode}", e.ApplicationExitCode);
        
        // 确保所有日志都被写入，设置超时避免无限等待
        try
        {
            // 使用异步方法并设置超时
            var flushTask = Log.CloseAndFlushAsync().AsTask();
            if (!flushTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Console.WriteLine("日志刷新超时，强制退出");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"日志刷新时发生错误: {ex.Message}");
        }
        
        // 正常退出，不使用强制退出
        // Environment.Exit(e.ApplicationExitCode); // 移除强制退出
    }
}