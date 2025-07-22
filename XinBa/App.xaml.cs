using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Weight;
using DeviceService.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using SharedUI.Views.Windows;
using XinBa.Services;
using XinBa.ViewModels;
using XinBa.ViewModels.Settings;
using XinBa.Views;
using XinBa.Views.Settings;

namespace XinBa;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = "Global\\XinBa_App_Mutex";
    private static Mutex? _mutex;
    private static bool _isShuttingDown;

    /// <summary>
    ///     创建主窗口 (返回 MainWindow)
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行 (移到 OnStartup 可能更合适，但这里也可以)
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (!createdNew)
        {
            Log.Warning("应用程序已在运行，将关闭此实例。");
            Current.Shutdown(1);
            return null!;
        }

        Log.Information("创建应用程序主窗口 (Shell)... ");
        return Container.Resolve<MainWindow>();
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 添加通用服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();

        // 注册 XinBa 需要的设备服务启动配置
        containerRegistry.AddPhotoCamera();
        containerRegistry.AddWeightScale();

        // 注册 HttpClientFactory
        var services = new ServiceCollection();
        services.AddHttpClient();
        var serviceProvider = services.BuildServiceProvider();
        containerRegistry.RegisterInstance(serviceProvider.GetRequiredService<IHttpClientFactory>());

        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterSingleton<MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();
        containerRegistry.RegisterForNavigation<VolumeSettingsView, VolumeSettingsViewModel>();
        containerRegistry.RegisterForNavigation<WeightSettingsView, WeightSettingsViewModel>();

        // 注册对话框
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");

        // 注册其他服务
        containerRegistry.RegisterSingleton<VolumeDataService>(); // VolumeDataHostedService 会用到

        // 注册二维码服务
        containerRegistry.RegisterSingleton<IQrCodeService, QrCodeService>();
        
        // 注册WildberriesApi服务
        containerRegistry.RegisterSingleton<ITareAttributesApiService, TareAttributesApiService>();

        // 注册后台服务启动器 (需要手动管理生命周期)
        Log.Debug("Registering background service singletons for manual management...");
        containerRegistry.RegisterSingleton<CameraStartupService>();
        // 注意：WeightStartupService 已在 AddWeightScale() 扩展方法中注册，避免重复注册
        containerRegistry.RegisterSingleton<VolumeDataHostedService>(); // XinBa specific volume service

        Log.Information("Type registration complete.");
    }

    /// <summary>
    ///     应用程序初始化完成后，直接启动后台服务
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        Log.Information("应用程序初始化完成 (OnInitialized)，直接启动后台服务。 ");

        // 获取主窗口并附加关闭事件处理
        var mainWindow = Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Closing += MainWindow_Closing;
            Log.Debug("主窗口已附加 Closing 事件处理 (OnInitialized)");
        }
        else
        {
            Log.Warning("无法获取主窗口实例以附加 Closing 事件处理程序。 ");
        }

        // 直接启动后台服务
        StartBackgroundServices();
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog (如果 Program.cs 不存在，这里是合适的位置)
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine("logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动 (OnStartup)");

        // 调用基类方法初始化容器等
        base.OnStartup(e);

        // 注册全局异常处理
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        Log.Information("DispatcherUnhandledException handler attached.");
    }



    /// <summary>
    ///     主窗口关闭事件处理程序
    /// </summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        Log.Information("MainWindow_Closing 事件触发。 IsShuttingDown: {IsShuttingDown}", _isShuttingDown);
        if (_isShuttingDown)
        {
            Log.Debug("已在关闭过程中，取消本次关闭事件处理。 ");
            return;
        }

        _isShuttingDown = true;
        e.Cancel = true;
        Log.Information("取消默认关闭，开始执行清理并显示等待窗口... ");

        ProgressIndicatorWindow? progressWindow = null;
        try
        {
            await Current.Dispatcher.InvokeAsync(() =>
            {
                Log.Debug("在 UI 线程上创建并显示 ProgressIndicatorWindow...");
                progressWindow = new ProgressIndicatorWindow("Closing application, please wait...")
                {
                    Owner = Current.MainWindow
                };
                progressWindow.Show();
                Log.Debug("ProgressIndicatorWindow 已显示。 ");
            });

            Log.Information("开始后台清理任务... ");
            await Task.Run(() =>
            {
                StopBackgroundServices(true);
                return Task.CompletedTask;
            });
            Log.Information("后台清理任务完成。 ");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行后台清理任务时发生错误 ");
        }
        finally
        {
            Log.Information("准备关闭等待窗口并真正关闭应用程序... ");
            await Current.Dispatcher.InvokeAsync(() => progressWindow?.Close());
            Log.Debug("ProgressIndicatorWindow 已关闭 (如果存在)。 ");

            await Current.Dispatcher.InvokeAsync(() =>
            {
                Log.Information("调用 Application.Current.Shutdown()...");
                Current.Shutdown();
            });
        }
    }



    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序退出处理程序 (OnExit) 开始... ExitCode: {ExitCode}", e.ApplicationExitCode);
        try
        {
            base.OnExit(e);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "调用 base.OnExit 时发生错误 ");
        }
        finally
        {
            Log.Information("释放 Mutex 并刷新日志... ");
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
            Log.CloseAndFlush();
        }
        Log.Information("应用程序退出处理程序 (OnExit) 完成。 ");
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled Dispatcher Exception Caught by App.xaml.cs");
        MessageBox.Show($"发生未处理的错误: {e.Exception.Message}\n\n应用程序可能不稳定。 ", "应用程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    ///     手动启动已注册的后台服务。
    /// </summary>
    private void StartBackgroundServices()
    {
        try
        {
            Log.Information("手动启动后台服务... ");
            var cameraStarter = Container.Resolve<CameraStartupService>();
            var weightStarter = Container.Resolve<WeightStartupService>();
            var volumeStarter = Container.Resolve<VolumeDataHostedService>();

            // 使用 Task.Run 在后台启动，避免阻塞 UI
            _ = Task.Run(() =>
            {
                try { cameraStarter.StartAsync(CancellationToken.None).Wait(); }
                catch (Exception ex) { Log.Error(ex, "启动 CameraStartupService 时出错 "); }
            });
            _ = Task.Run(() =>
            {
                try { weightStarter.StartAsync(CancellationToken.None).Wait(); }
                catch (Exception ex) { Log.Error(ex, "启动 WeightStartupService 时出错 "); }
            });
            _ = Task.Run(() =>
            {
                try { volumeStarter.StartAsync(CancellationToken.None).Wait(); }
                catch (Exception ex) { Log.Error(ex, "启动 VolumeDataHostedService 时出错 "); }
            });

            Log.Information("后台服务启动已发起。 ");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析或发起后台服务启动时出错。 ");
        }
    }

    /// <summary>
    ///     手动停止已注册的后台服务。
    /// </summary>
    /// <param name="waitForCompletion">是否等待服务停止完成。</param>
    private void StopBackgroundServices(bool waitForCompletion)
    {
        Log.Information("手动停止后台服务... Wait for completion: {WaitForCompletion}", waitForCompletion);
        try
        {
            var cameraStarter = Container?.Resolve<CameraStartupService>();
            var weightStarter = Container?.Resolve<WeightStartupService>();
            var volumeStarter = Container?.Resolve<VolumeDataHostedService>();

            var tasks = new List<Task>();
            if (cameraStarter != null)
                tasks.Add(Task.Run(() =>
                {
                    try { cameraStarter.StopAsync(CancellationToken.None).Wait(); }
                    catch (Exception ex) { Log.Error(ex, "停止 CameraStartupService 时出错 "); }
                }));
            if (weightStarter != null)
                tasks.Add(Task.Run(() =>
                {
                    try { weightStarter.StopAsync(CancellationToken.None).Wait(); }
                    catch (Exception ex) { Log.Error(ex, "停止 WeightStartupService 时出错 "); }
                }));
            if (volumeStarter != null)
                tasks.Add(Task.Run(() =>
                {
                    try { volumeStarter.StopAsync(CancellationToken.None).Wait(); }
                    catch (Exception ex) { Log.Error(ex, "停止 VolumeDataHostedService 时出错 "); }
                }));

            if (tasks.Count != 0 && waitForCompletion)
            {
                try
                {
                    // 等待所有停止任务完成，最多等待 5 秒
                    if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5)))
                    {
                        Log.Warning("一个或多个后台服务未在超时时间内正常停止。 ");
                    }
                    else
                    {
                        Log.Information("后台服务已停止。 ");
                    }
                }
                catch (AggregateException aex)
                {
                    Log.Error(aex, "等待后台服务停止时发生聚合错误。 ");
                    foreach (var innerEx in aex.InnerExceptions)
                    {
                        Log.Error(innerEx, "  内部停止错误: ");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待后台服务停止时发生错误。 ");
                }
            }
            else if (!waitForCompletion)
            {
                Log.Information("后台服务停止已发起 (不等待完成)。 ");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析或发起后台服务停止时出错。 ");
        }
    }
}