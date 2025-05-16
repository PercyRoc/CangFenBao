using System.Globalization;
using System.Windows;
using SharedUI.Views.Windows;
using SortingServices.Modules;
using WeiCiModule.ViewModels;
using WeiCiModule.Views;
using Serilog;
using Common.Services.Settings;
using SortingServices.Modules.Models;
using Common.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using Microsoft.Extensions.Configuration;
using System.IO;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;
using Camera;

namespace WeiCiModule;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\WeiCiModule_App_Mutex_B3A7F8D1-C2E9-4B5A-9A1D-F8C7E0A1B2C3";
    private static ResxLocalizationProvider ResxProvider { get; } = ResxLocalizationProvider.Instance;

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
            Log.Fatal(args.Exception, "应用程序发生未经处理的致命异常!");
            args.Handled = true;
            MessageBox.Show($"发生了一个致命错误，应用程序即将关闭。详情请查看日志。", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(-1);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "后台任务发生未观察到的异常!");
            args.SetObserved();
        };
        
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            Log.Fatal(exception, "AppDomain 级别捕获到未经处理的致命异常! IsTerminating: {IsTerminating}", args.IsTerminating);
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

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.AddCommonServices();
        containerRegistry.RegisterSingleton<SettingsDialogViewModel>();
        containerRegistry.RegisterSingleton<ChuteSettingsViewModel>();
        containerRegistry.RegisterSingleton<ModulesTcpSettingsViewModel>();
        containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<ChuteSettingsView, ChuteSettingsViewModel>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();
        containerRegistry.RegisterForNavigation<ModulesTcpSettingsView, ModulesTcpSettingsViewModel>();
        containerRegistry.RegisterSingleton<IModuleConnectionService, ModuleConnectionService>();
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
                var tcpSettings = settingsService.LoadSettings<ModelsTcpSettings>();
                if (!string.IsNullOrEmpty(tcpSettings.Address))
                {
                    Log.Information("正在启动 ModuleConnectionService，监听地址: {IpAddress}:{Port}...", tcpSettings.Address, tcpSettings.Port);
                    bool started = await moduleConnectionService.StartServerAsync(tcpSettings.Address, tcpSettings.Port);
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
        });
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<SpecificIntegratedCameraModule>();
        Log.Information("海康物流SDK 已添加到模块目录。");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序正在准备退出...");
        var tasks = new List<Task>();

        var moduleConnectionService = ContainerLocator.Container.Resolve<IModuleConnectionService>();
        Log.Information("正在尝试停止 ModuleConnectionService...");
        tasks.Add(Task.Run(async () => 
        {
            try
            {
                await moduleConnectionService.StopServerAsync();
                Log.Information("ModuleConnectionService 已成功停止。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止 ModuleConnectionService 时发生意外错误。");
            }
        }));

        if (tasks.Count != 0)
        {
            Log.Information("等待后台服务停止...");
            try
            {
                if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(3)))
                {
                    Log.Warning("一个或多个后台服务未在3秒超时内完全停止。");
                }
                else
                {
                    Log.Information("所有请求停止的后台服务均已处理完毕。");
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
        Log.CloseAndFlush();
    }
}