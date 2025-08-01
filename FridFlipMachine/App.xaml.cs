using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using Common.Models.Settings;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Rfid;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using FridFlipMachine.Models;
using FridFlipMachine.Services;
using FridFlipMachine.ViewModels;
using FridFlipMachine.ViewModels.Settings;
using FridFlipMachine.Views;
using FridFlipMachine.Views.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Dialogs;
using SharedUI.Views.Settings;
using WPFLocalizeExtension.Engine;

namespace FridFlipMachine;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private const string MutexName = "Global\\PlateTurnoverMachine_App_Mutex";
    private static Mutex? _mutex;

    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew) return Container.Resolve<MainWindow>();

        // 关闭当前实例
        Current.Shutdown();
        return null!;
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<PlateTurnoverSettingsView, PlateTurnoverSettingsViewModel>();
        containerRegistry.RegisterForNavigation<FridSettingsView, FridSettingsViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddFridDevice();

        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<HttpClient>();

        // 注册Frid包裹服务（替代PackageTransferService）
        containerRegistry.RegisterSingleton<FridPackageService>();

        // 注册设置窗口
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
        
        // 注册历史记录对话框
        containerRegistry.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>("HistoryDialog");

        // 注册TCP连接服务
        containerRegistry.RegisterSingleton<ITcpConnectionService, TcpConnectionService>();

        containerRegistry.RegisterSingleton<SortingService>();
        containerRegistry.RegisterSingleton<PlateTurnoverSettings>();
        containerRegistry.RegisterSingleton<TcpConnectionHostedService>();
        containerRegistry.RegisterSingleton<FridSettingsViewModel>();
        containerRegistry.Register<IHostedService>(static sp => sp.Resolve<TcpConnectionHostedService>());
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 设置本地化为中文
        var culture = new CultureInfo("zh-CN");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        
        // 初始化 WPFLocalizeExtension
        LocalizeDictionary.Instance.Culture = culture;
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024, // Limit file size to 10MB
                retainedFileCountLimit: 31) // Retain logs for 30 days (31 files total)
            .CreateLogger();

        // 注册全局异常处理
        RegisterGlobalExceptionHandling();

        Log.Information("应用程序启动");
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        // 启动异步初始化
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 启动托管服务
            var hostedService = Container.Resolve<TcpConnectionHostedService>();
            await hostedService.StartAsync(CancellationToken.None);

            // 初始化并启动Frid服务
            try
            {
                var fridService = Container.Resolve<IFridService>();
                var settingsService = Container.Resolve<ISettingsService>();
                var fridSettings = settingsService.LoadSettings<FridSettings>();

                // 检查配置是否为空或无效
                if (fridSettings == null)
                {
                    Log.Warning("Frid配置为空，跳过Frid服务初始化");
                    MessageBox.Show("Frid配置为空，请先在设置中配置Frid设备参数。", "Frid配置提示", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (fridSettings.IsEnabled)
                {
                    Log.Information("开始初始化Frid服务...");
                    Log.Information("Frid配置: 连接类型={ConnectionType}, 启用状态={IsEnabled}", 
                        fridSettings.ConnectionType, fridSettings.IsEnabled);
                    
                    if (fridSettings.ConnectionType == FridConnectionType.Tcp)
                    {
                        Log.Information("TCP配置: IP={IpAddress}, 端口={Port}", 
                            fridSettings.TcpIpAddress, fridSettings.TcpPort);
                        
                        // 检查TCP配置是否有效
                        if (string.IsNullOrWhiteSpace(fridSettings.TcpIpAddress) || fridSettings.TcpPort <= 0)
                        {
                            Log.Warning("TCP配置无效: IP地址为空或端口号无效");
                            MessageBox.Show("Frid TCP配置无效，请检查IP地址和端口号设置。", "Frid配置错误", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    else
                    {
                        Log.Information("串口配置: 端口={PortName}, 波特率={BaudRate}, 数据位={DataBits}, 停止位={StopBits}, 校验位={Parity}", 
                            fridSettings.SerialPortName, fridSettings.BaudRate, fridSettings.DataBits, 
                            fridSettings.StopBits, fridSettings.Parity);
                        
                        // 检查串口配置是否有效
                        if (string.IsNullOrWhiteSpace(fridSettings.SerialPortName) || fridSettings.BaudRate <= 0)
                        {
                            Log.Warning("串口配置无效: 端口名称为空或波特率无效");
                            MessageBox.Show("Frid串口配置无效，请检查端口名称和波特率设置。", "Frid配置错误", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    
                    // 初始化Frid服务
                    var initResult = await fridService.InitializeAsync(fridSettings);
                    if (initResult)
                    {
                        Log.Information("Frid服务初始化成功");
                        
                        // 连接Frid设备
                        var connectResult = await fridService.ConnectAsync();
                        if (connectResult)
                        {
                            Log.Information("Frid设备连接成功");
                            
                            // 设置工作参数
                            var paramResult = await fridService.SetWorkingParamAsync(fridSettings.Power);
                            if (paramResult)
                            {
                                Log.Information("Frid工作参数设置成功，功率: {Power}dBm", fridSettings.Power);
                            }
                            else
                            {
                                Log.Warning("Frid工作参数设置失败");
                            }
                            
                            // 开始盘点
                            var inventoryResult = await fridService.StartInventoryAsync();
                            if (inventoryResult)
                            {
                                Log.Information("Frid盘点启动成功");
                            }
                            else
                            {
                                Log.Warning("Frid盘点启动失败");
                            }
                        }
                        else
                        {
                            Log.Error("Frid设备连接失败");
                        }
                    }
                    else
                    {
                        Log.Error("Frid服务初始化失败");
                    }
                }
                else
                {
                    Log.Information("Frid服务已禁用，跳过初始化");
                }
            }
            catch (Exception fridEx)
            {
                Log.Error(fridEx, "初始化Frid服务时发生错误: {Message}", fridEx.Message);
                Log.Error("异常堆栈: {StackTrace}", fridEx.StackTrace);
                
                // 如果是内部异常，也记录内部异常信息
                if (fridEx.InnerException != null)
                {
                    Log.Error("内部异常: {InnerMessage}", fridEx.InnerException.Message);
                    Log.Error("内部异常堆栈: {InnerStackTrace}", fridEx.InnerException.StackTrace);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动服务时发生错误");
            // 这里可以添加其他错误处理逻辑，比如显示错误对话框
            MessageBox.Show($"启动服务时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     执行异步关闭操作
    /// </summary>
    public async Task PerformShutdownAsync()
    {
        try
        {
            Log.Information("开始执行异步关闭操作...");

            // 0. 新增：复位所有格口
            try
            {
                var sortingService = Container.Resolve<SortingService>();
                await sortingService.ResetAllChutesAsync();
            }
            catch (Exception resetEx)
            {
                Log.Error(resetEx, "复位所有格口时发生错误");
            }

            // 1. 停止Frid服务
            try
            {
                var fridService = Container.Resolve<IFridService>();
                if (fridService.IsConnected)
                {
                    Log.Information("开始停止Frid服务...");
                    
                    // 停止盘点
                    await fridService.StopInventoryAsync();
                    Log.Information("Frid盘点已停止");
                    
                    // 断开连接
                    await fridService.DisconnectAsync();
                    Log.Information("Frid设备已断开连接");
                }
                else
                {
                    Log.Information("Frid设备未连接，跳过停止操作");
                }
            }
            catch (Exception fridEx)
            {
                Log.Error(fridEx, "停止Frid服务时发生错误: {Message}", fridEx.Message);
                Log.Error("异常堆栈: {StackTrace}", fridEx.StackTrace);
                
                // 如果是内部异常，也记录内部异常信息
                if (fridEx.InnerException != null)
                {
                    Log.Error("内部异常: {InnerMessage}", fridEx.InnerException.Message);
                    Log.Error("内部异常堆栈: {InnerStackTrace}", fridEx.InnerException.StackTrace);
                }
            }

            // 2. 停止托管服务
            try
            {
                var hostedService = Container.Resolve<TcpConnectionHostedService>();
                await hostedService.StopAsync(CancellationToken.None);
            }
            catch (Exception svcEx)
            {
                Log.Error(svcEx, "停止TCP连接托管服务时发生错误");
            }

            // 等待所有日志写入完成
            Log.Information("异步关闭操作完成，正在刷新日志...");
            await Log.CloseAndFlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行异步关闭操作时发生错误");
            try
            {
                await Log.CloseAndFlushAsync(); // 尝试刷新日志
            }
            catch (Exception logEx)
            {
                Console.WriteLine(@$"刷新日志时也发生错误: {logEx}");
            }
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // OnExit 主要用于非常快速、同步的最终清理
        Log.Information("应用程序退出事件触发");
        try
        {
            // 注意：避免在此处执行复杂的或异步的清理逻辑
        }
        finally
        {
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            Log.Information("Mutex 已释放");
            base.OnExit(e);
        }
    }

    /// <summary>
    ///     注册全局异常处理程序
    /// </summary>
    private void RegisterGlobalExceptionHandling()
    {
        // UI线程未处理异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 非UI线程未处理异常
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Task线程内未处理异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "UI线程发生未经处理的异常 (Dispatcher Unhandled Exception)");
        // 显示一个友好的对话框
        MessageBox.Show($"发生了一个无法恢复的严重错误，应用程序即将退出。\n\n错误信息：{e.Exception.Message}",
            "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true; // 标记为已处理，防止默认的崩溃对话框

        // 尝试优雅地关闭
        _ = PerformShutdownAsync().ContinueWith(_ =>
        {
            Current.Dispatcher.Invoke(Current.Shutdown);
        });
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Log.Fatal(exception, "非UI线程发生未经处理的异常 (AppDomain Unhandled Exception). IsTerminating: {IsTerminating}", e.IsTerminating);

        // 如果应用程序即将终止，确保日志被写入
        if (e.IsTerminating)
        {
            Log.CloseAndFlush();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "一个后台任务发生未经观察的异常 (Unobserved Task Exception)");
        e.SetObserved(); // 防止进程终止
    }
}