using System.Net.Http;
using System.Windows;
using CommonLibrary.Extensions;
using CommonLibrary.Services;
using DeviceService.Camera;
using Presentation_CommonLibrary.Extensions;
using Presentation_CommonLibrary.Services;
using Presentation_XinBa.Services;
using Presentation_XinBa.Services.Models;
using Presentation_XinBa.ViewModels;
using Presentation_XinBa.ViewModels.Settings;
using Presentation_XinBa.Views;
using Presentation_XinBa.Views.Settings;
using Prism.Ioc;
using Prism.Services.Dialogs;
using Serilog;

namespace Presentation_XinBa;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private bool _isLoggedIn = false;
    
    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已登录
        var apiService = Container.Resolve<IApiService>();
        _isLoggedIn = apiService.IsLoggedIn();
        
        if (!_isLoggedIn)
        {
            // 如果未登录，创建并返回登录窗口
            Log.Information("未检测到登录状态，返回登录窗口作为主窗口");
            
            // 创建LoginViewModel
            var loginViewModel = Container.Resolve<LoginViewModel>();
            Log.Debug("已创建LoginViewModel实例");
            
            // 创建登录窗口
            var loginDialog = new LoginDialog
            {
                DataContext = loginViewModel
            };
            Log.Debug("已创建LoginDialog实例并设置DataContext");
            
            return loginDialog;
        }
        
        // 如果已登录，返回主窗口
        Log.Information("检测到已登录状态，返回主应用窗口");
        return Container.Resolve<MainWindow>();
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 添加通用服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddPresentationCommonServices();
        
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
        
        containerRegistry.RegisterSingleton<HttpClient>();
        
        // 注册设置服务
        containerRegistry.RegisterSingleton<ISettingsService, JsonSettingsService>();
        
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
    protected override async void OnStartup(StartupEventArgs e)
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
        
        // 如果已登录，启动相机服务
        if (_isLoggedIn)
        {
            var apiService = Container.Resolve<IApiService>();
            Log.Information("检测到已登录状态，员工ID: {EmployeeId}", apiService.GetCurrentEmployeeId());
            await StartCameraServiceAsync();
        }
        else
        {
            // 监听登录窗口的关闭事件
            if (MainWindow is LoginDialog loginDialog)
            {
                loginDialog.Closed += async (sender, args) =>
                {
                    // 检查是否登录成功
                    var apiService = Container.Resolve<IApiService>();
                    if (apiService.IsLoggedIn())
                    {
                        Log.Information("登录成功，准备启动主窗口和相机服务");
                        
                        // 创建并显示主窗口
                        MainWindow = Container.Resolve<MainWindow>();
                        MainWindow.Show();
                        
                        // 启动相机服务
                        await StartCameraServiceAsync();
                    }
                    else
                    {
                        Log.Information("登录取消或失败，退出应用");
                        Current.Shutdown();
                    }
                };
            }
        }
    }
    
    /// <summary>
    /// 启动相机服务
    /// </summary>
    private async Task StartCameraServiceAsync()
    {
        // 启动TCP相机后台服务
        TcpCameraBackgroundService? cameraService = Container.Resolve<TcpCameraBackgroundService>();
        await cameraService.StartAsync(CancellationToken.None);
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");
            
            // 停止TCP相机后台服务
            var cameraService = Container.Resolve<TcpCameraBackgroundService>();
            cameraService.StopAsync(CancellationToken.None).Wait();
            
            // 登出当前用户
            var apiService = Container.Resolve<IApiService>();
            if (apiService.IsLoggedIn())
            {
                Log.Information("正在登出用户...");
                apiService.LogoutAsync().Wait();
            }
            
            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();

            // 确保所有操作完成
            Thread.Sleep(1000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
            Thread.Sleep(1000);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}