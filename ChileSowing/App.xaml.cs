using System.Windows;
using History;
using SowingSorting;
using ChileSowing.Views;
using Serilog;
using Microsoft.Extensions.Configuration;
using ChileSowing.ViewModels;
using Common.Services.Settings;
using Common.Services.Ui;
using SowingSorting.Services;
using SharedUI.Views.Windows;

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

        // 单实例互斥体
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Warning("已有实例在运行，程序即将退出。");
            MessageBox.Show("智利播种墙已在运行。", "已启动", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }

        base.OnStartup(e);

        // Set application culture to English
        try
        {
            var culture = new System.Globalization.CultureInfo("en-US");
            WPFLocalizeExtension.Engine.LocalizeDictionary.Instance.Culture = culture;
            Log.Information($"Application culture set to {culture.Name}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error setting application culture");
        }
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
        
        containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        containerRegistry.RegisterDialog<ChuteDetailDialogView, ChuteDetailDialogViewModel>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();

        containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();
        containerRegistry.RegisterSingleton<IModbusTcpService, ModbusTcpService>();
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        // 注册历史模块
        moduleCatalog.AddModule<HistoryModule>();
        // 注册播种分拣模块
        moduleCatalog.AddModule<SowingSortingModule>();
    }

    protected override async void OnInitialized()
    {
        base.OnInitialized();
        // 初始化历史数据库服务（必须，见history_module_guide）
        var historyService = Container.Resolve<History.Data.IPackageHistoryDataService>();
        await historyService.InitializeAsync();
    }
}