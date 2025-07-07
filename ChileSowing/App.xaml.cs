using System.Windows;
using History;
using SowingSorting;
using ChileSowing.Views;
using Serilog;
using Microsoft.Extensions.Configuration;
using ChileSowing.ViewModels;
using Common.Services.Settings;
using SowingSorting.Services;
using Common;
using ChileSowing.Services;
using System.Net.Http;
using ChileSowing.ViewModels.Settings;
using System.Globalization;
using Common.Services.Notifications;
using WPFLocalizeExtension.Engine;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace ChileSowing;

/// <summary>
/// 智利播种墙 Prism 启动类，注册历史模块和播种分拣模块
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\ChileSowing_App_Mutex_8A1B2C3D-4E5F-6789-ABCD-1234567890EF";
    private IConfiguration _configuration = null!; // 保存配置实例

    protected override void OnStartup(StartupEventArgs e)
    {
        // Step 1: 构建配置
        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Step 2: 使用配置初始化 Serilog 的静态 Logger
        // 确保在任何日志记录尝试之前完成此操作
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(_configuration) // 从 IConfiguration 读取配置
            .CreateLogger();

        Log.Information("Serilog Logger 已通过 appsettings.json 配置初始化。");

        // 设置本地化为中文
        var culture = new CultureInfo("zh-CN");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.Culture = culture;
        Log.Information("本地化语言设置为中文 (zh-CN)");

        // 单实例互斥体
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Warning("已有实例在运行，程序即将退出。");
            MessageBox.Show("智利播种墙已在运行。", "已启动", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序正在退出...");
        try
        {
            // 优雅断开Modbus TCP服务
            var modbusService = Container.Resolve<IModbusTcpService>();
            {
                Log.Information("正在断开IModbusTcpService...");
                var task = modbusService.DisconnectAsync();
                if (!task.Wait(TimeSpan.FromSeconds(3)))
                {
                    Log.Warning("IModbusTcpService 断开超时。");
                }
                else
                {
                    Log.Information("IModbusTcpService 已断开。");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开IModbusTcpService时发生异常。");
        }
        
        try
        {
            // 停止Web服务器
            var webServerService = Container.Resolve<IWebServerService>();
            {
                Log.Information("正在停止Web服务器...");
                var task = webServerService.StopAsync();
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                {
                    Log.Warning("Web服务器停止超时。");
                }
                else
                {
                    Log.Information("Web服务器已停止。");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止Web服务器时发生异常。");
        }
        _mutex?.ReleaseMutex();
        _mutex = null;
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    protected override Window CreateShell()
    {
        // 启动所有模块，确保自动加载（容器已初始化，此处安全）
        var moduleManager = Container.Resolve<IModuleManager>();
        moduleManager.Run();
        // 返回主窗口
        return Container.Resolve<MainWindow>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // Step 3: 注册已配置的 IConfiguration 和 ILogger 实例
        containerRegistry.RegisterInstance(_configuration); // 注册已创建和使用的 IConfiguration
        containerRegistry.RegisterInstance(Log.Logger); // 注册已配置的静态 Log.Logger 实例
        
        // 注册 Microsoft.Extensions.Logging for Serilog
        containerRegistry.RegisterInstance<ILoggerFactory>(new SerilogLoggerFactory(null, true));
        containerRegistry.Register(typeof(ILogger<>), typeof(Logger<>));
        
        containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<ChuteDetailDialogView, ChuteDetailDialogViewModel>();

        containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();
        containerRegistry.RegisterSingleton<IModbusTcpService, ModbusTcpService>();
        
        // 确保 IEventAggregator 被注册（Prism应该自动注册，但显式注册以确保可用）
        containerRegistry.RegisterSingleton<IEventAggregator, EventAggregator>();
        
        // 注册HttpClient和快手API服务
        containerRegistry.RegisterSingleton<HttpClient>();
        containerRegistry.RegisterSingleton<IKuaiShouApiService, KuaiShouApiService>();
        
        // 注册KuaiShouSettingsViewModel
        containerRegistry.RegisterSingleton<KuaiShouSettingsViewModel>();
        
        // 注册WebServerSettingsViewModel
        containerRegistry.RegisterSingleton<WebServerSettingsViewModel>();
        
        // 注册本地化服务
        containerRegistry.RegisterSingleton<ILocalizationService, LocalizationService>();
        
        // 注册Web服务器服务
        containerRegistry.RegisterSingleton<IWebServerService, WebServerService>();
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<CommonServicesModule>();
        // 注册历史模块
        moduleCatalog.AddModule<HistoryModule>();
        // 注册播种分拣模块
        moduleCatalog.AddModule<SowingSortingModule>();
    }

    protected override async void OnInitialized()
    {
        base.OnInitialized();
        // 语言设置硬编码为中文（简体）
        Log.Information("Language set to Chinese (Simplified)");
        
        var historyService = Container.Resolve<History.Data.IPackageHistoryDataService>();
        await historyService.InitializeAsync();
        
        // 启动Web服务器
        var webServerService = Container.Resolve<IWebServerService>();
        await webServerService.StartAsync();
    }
}