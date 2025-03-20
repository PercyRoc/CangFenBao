using System.Windows;
using Common.Extensions;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using XinBa.Services;
using XinBa.ViewModels;
using XinBa.ViewModels.Settings;
using XinBa.Views;
using XinBa.Views.Settings;

namespace XinBa;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "XinBa_App_Mutex";
    private Window? _currentMainWindow;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (!createdNew)
        {
            // 关闭当前实例
            Current.Shutdown();
            return null!;
        }

        // 始终显示登录窗口
        Log.Information("应用程序启动，显示登录窗口");

        var window = Container.Resolve<Window>("LoginDialog");

        // 设置DataContext
        var viewModel = Container.Resolve<LoginViewModel>();
        window.DataContext = viewModel;

        // 订阅登录成功事件
        viewModel.LoginSucceeded += OnLoginSucceeded;

        _currentMainWindow = window;
        Current.MainWindow = window;
        return window;
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 添加通用服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();

        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterSingleton<MainWindowViewModel>();
        containerRegistry.Register<CameraSettingsView>();
        containerRegistry.Register<CameraSettingsViewModel>();

        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();

        // 注册登录窗口
        containerRegistry.Register<Window, LoginDialog>("LoginDialog");
        containerRegistry.Register<LoginViewModel>();

        // 注册API服务
        containerRegistry.RegisterSingleton<IApiService, ApiService>();

        // 注册TCP相机服务
        containerRegistry.RegisterSingleton<TcpCameraService>();

        // 注册TCP相机后台服务
        containerRegistry.RegisterSingleton<TcpCameraBackgroundService>();
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动");

        // 先调用基类方法初始化容器
        base.OnStartup(e);

        // 注册全局异常处理
        Current.DispatcherUnhandledException += static (_, args) =>
        {
            Log.Error(args.Exception, "未处理的异常");
            args.Handled = true;
        };
    }

    /// <summary>
    ///     登录成功事件处理
    /// </summary>
    private async void OnLoginSucceeded(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("登录成功，准备切换到主窗口");

            // 使用Dispatcher延迟创建和显示主窗口
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // 保存当前窗口引用
                    var oldWindow = _currentMainWindow;

                    // 创建主窗口和ViewModel
                    var mainWindow = Container.Resolve<MainWindow>();
                    var viewModel = Container.Resolve<MainWindowViewModel>();

                    // 设置DataContext
                    mainWindow.DataContext = viewModel;
                    Log.Information("已设置MainWindow的DataContext为MainWindowViewModel");

                    _currentMainWindow = mainWindow;

                    // 订阅登出事件
                    viewModel.LogoutRequested += OnLogoutRequested;

                    // 显示主窗口并设置为应用程序的主窗口
                    mainWindow.Show();
                    Current.MainWindow = mainWindow;

                    // 启动相机服务
                    await StartCameraServiceAsync();

                    // 隐藏登录窗口而不是关闭它
                    if (oldWindow != null)
                    {
                        // 如果是LoginDialog，需要手动隐藏，因为我们不再通过RequestClose事件关闭
                        oldWindow.Hide();
                        Log.Information("登录窗口已隐藏");
                    }

                    // 显式调用MainWindowViewModel的UpdateCurrentEmployeeInfo方法以更新员工信息
                    _ = viewModel.UpdateCurrentEmployeeInfo();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "创建主窗口时发生错误");
                    Current.Shutdown();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理登录成功事件时发生错误");
        }
    }

    /// <summary>
    ///     登出请求事件处理
    /// </summary>
    private async void OnLogoutRequested(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("收到登出请求，准备切换到登录窗口");

            // 使用Dispatcher延迟创建和显示登录窗口
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // 保存当前窗口引用
                    var oldWindow = _currentMainWindow;

                    // 创建登录窗口
                    var window = Container.Resolve<Window>("LoginDialog");

                    // 设置DataContext
                    var viewModel = Container.Resolve<LoginViewModel>();
                    window.DataContext = viewModel;
                    Log.Information("已设置LoginDialog的DataContext为LoginViewModel");

                    _currentMainWindow = window;

                    // 订阅登录成功事件
                    viewModel.LoginSucceeded += OnLoginSucceeded;

                    // 显示登录窗口并设置为应用程序的主窗口
                    window.Show();
                    Current.MainWindow = window;
                    Log.Information("登录窗口已显示");

                    // 等待一小段时间，确保登录窗口完全显示
                    await Task.Delay(100);

                    // 停止相机服务
                    var cameraService = Container.Resolve<TcpCameraBackgroundService>();
                    await cameraService.StopAsync(CancellationToken.None);
                    Log.Information("相机服务已停止");

                    // 隐藏主窗口而不是关闭它
                    if (oldWindow != null)
                    {
                        oldWindow.Hide();
                        Log.Information("主窗口已隐藏");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "创建登录窗口时发生错误");
                    Current.Shutdown();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理登出请求事件时发生错误");
        }
    }

    /// <summary>
    ///     启动相机服务
    /// </summary>
    private async Task StartCameraServiceAsync()
    {
        try
        {
            var cameraService = Container.Resolve<TcpCameraBackgroundService>();
            await cameraService.StartAsync(CancellationToken.None);
            Log.Information("相机服务已启动");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务时发生错误");
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");
            // 关闭所有窗口，无论是否可见
            foreach (Window window in Current.Windows)
                try
                {
                    if (window == Current.MainWindow) continue;

                    window.Close();
                    Log.Information($"已关闭窗口: {window.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"关闭窗口时发生错误: {window.GetType().Name}");
                }

            // 停止TCP相机后台服务
            var cameraService = Container.Resolve<TcpCameraBackgroundService>();
            if (cameraService != null)
                try
                {
                    cameraService.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
                    Log.Information("TCP相机服务已停止");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止TCP相机服务时发生错误");
                }

            // 登出当前用户
            var apiService = Container.Resolve<IApiService>();
            if (apiService == null || !apiService.IsLoggedIn()) return;

            {
                try
                {
                    Log.Information("正在登出用户...");
                    var task = apiService.LogoutAsync();
                    if (!task.Wait(TimeSpan.FromSeconds(5)))
                        Log.Warning("登出操作超时");
                    else
                        Log.Information("用户已登出");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "登出用户时发生错误");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
        }
        finally
        {
            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}