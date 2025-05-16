using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using XinBa.Services;
using XinBa.ViewModels;
using XinBa.Views;
using System.ComponentModel;
using SharedUI.Views.Windows;
using DeviceService.DataSourceDevices.Camera.TCP;

namespace XinBa;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "Global\\XinBa_App_Mutex";
    private static bool _isShuttingDown;

    /// <summary>
    /// 创建主窗口 (返回 MainWindow)
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
    /// 注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 添加通用服务
        containerRegistry.AddCommonServices();

        // 注册 HttpClientFactory
        var services = new ServiceCollection();
        services.AddHttpClient();
        var serviceProvider = services.BuildServiceProvider();
        containerRegistry.RegisterInstance(serviceProvider.GetRequiredService<IHttpClientFactory>());
        containerRegistry.RegisterSingleton<MainWindowViewModel>();
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterSingleton<MainWindowViewModel>();
        containerRegistry.RegisterSingleton<TcpCameraService>();
        containerRegistry.RegisterDialog<LoginDialog, LoginViewModel>("LoginDialog");

        // 注册其他服务
        containerRegistry.RegisterSingleton<IApiService, ApiService>();

        Log.Information("Type registration complete.");
    }

    /// <summary>
    /// 应用程序初始化完成后，显示登录对话框并启动后台服务
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized(); // This might show the shell depending on Prism version/config
        Log.Information("应用程序初始化完成 (OnInitialized)，准备显示登录对话框。 ");

        // 隐藏主窗口，直到登录成功
        var mainWindow = Current.MainWindow;
        if (mainWindow != null)
        {
            mainWindow.Hide();
            mainWindow.Closing += MainWindow_Closing;
            Log.Debug("主窗口已隐藏并附加 Closing 事件处理 (OnInitialized)");
        }
        else
        {
            Log.Warning("无法获取主窗口实例以附加 Closing 事件处理程序。 ");
        }

        var dialogService = Container.Resolve<IDialogService>();

        // 显示登录对话框
        dialogService.ShowDialog("LoginDialog", null, result =>
        {
            Log.Information("登录对话框关闭回调执行，结果: {DialogResult}", result?.Result);

            if (result?.Result == ButtonResult.OK)
            {
                Log.Information("登录成功 (回调确认)，显示主窗口并启动后台服务。 ");
                // 登录成功，显示主窗口
                mainWindow?.Show();
                Log.Debug("主窗口已显示");

                // 在这里执行主窗口显示后的操作
                if (mainWindow?.DataContext is MainWindowViewModel mainVm)
                {
                    _ = mainVm.UpdateCurrentEmployeeInfo();
                    // 订阅登出事件
                    mainVm.LogoutRequested += OnLogoutRequested;
                    Log.Debug("MainWindowViewModel 更新并已订阅登出事件。 ");
                }
                else
                {
                    Log.Warning("无法获取 MainWindowViewModel 实例来更新员工信息或订阅事件。 ");
                }

                // 手动启动后台服务
                StartBackgroundServices();
            }
            else
            {
                Log.Warning("登录失败或取消，应用程序将关闭。 ");
                Current.Shutdown(2); // 使用适当的退出代码
            }
        });
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
    ///     登出请求事件处理
    /// </summary>
    private void OnLogoutRequested(object? sender, EventArgs e)
    {
        try
        {
            Log.Information("收到登出请求，准备切换到登录窗口 ");

            // 停止后台服务
            StopBackgroundServices(); // Don't wait indefinitely on logout

            // 关闭主窗口（或隐藏）并显示登录对话框
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var currentMainWindow = Current.MainWindow;
                    var dialogService = Container.Resolve<IDialogService>();

                    // 注销事件
                    if (sender is MainWindowViewModel mainVm)
                    {
                        mainVm.LogoutRequested -= OnLogoutRequested;
                    }

                    currentMainWindow?.Hide();
                    Log.Debug("主窗口已隐藏 (Logout)");

                    // 显示登录对话框
                    dialogService.ShowDialog("LoginDialog", null, loginResult =>
                    {
                        if (loginResult.Result == ButtonResult.OK)
                        {
                            Log.Information("重新登录成功，显示主窗口并重启后台服务。 ");
                            // 重新显示主窗口
                            currentMainWindow?.Show();
                            if (currentMainWindow?.DataContext is MainWindowViewModel newMainVm)
                            {
                                _ = newMainVm.UpdateCurrentEmployeeInfo();
                                // 重新订阅事件
                                newMainVm.LogoutRequested += OnLogoutRequested;
                            }
                            // 重启后台服务
                            StartBackgroundServices(); 
                        }
                        else
                        {
                            Log.Warning("重新登录失败或取消，应用程序将关闭。 ");
                            Current.Shutdown(2);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "切换到登录窗口时发生错误 ");
                    Current.Shutdown(); // 关闭以防出错
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理登出请求事件时发生错误 ");
        }
    }

    /// <summary>
    /// 主窗口关闭事件处理程序
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
            await Task.Run(async () =>
            {
                StopBackgroundServices();
                await LogoutApiUserAsync();
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
    /// 尝试登出 API 用户
    /// </summary>
    private async Task LogoutApiUserAsync()
    {
        try
        {
            var apiService = Container?.Resolve<IApiService>();
            if (apiService != null && apiService.IsLoggedIn())
            {
                Log.Information("正在登出 API 用户... ");
                try
                {
                    await apiService.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Log.Information("API 用户已登出或超时。 ");
                }
                catch (TimeoutException)
                {
                    Log.Warning("API 登出操作超时 ");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "API 登出时发生错误 ");
                }
            }
            else
            {
                Log.Debug("无需登出 API 用户 (未登录或服务不可用) ");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 ApiService 以进行登出时发生错误 ");
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
    /// 手动启动已注册的后台服务。
    /// </summary>
    private void StartBackgroundServices()
    {
        var tcpCameraService = Container.Resolve<TcpCameraService>();
        tcpCameraService.Start();
    }

    /// <summary>
    /// 手动停止已注册的后台服务。
    /// </summary>
    private void StopBackgroundServices()
    {
        var tcpCameraService = Container.Resolve<TcpCameraService>();
        tcpCameraService.Stop();
    }
}