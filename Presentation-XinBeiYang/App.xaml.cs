using System.Net.Http;
using System.Windows;
using CommonLibrary.Extensions;
using DeviceService;
using Presentation_CommonLibrary.Extensions;
using Presentation_XinBeiYang.ViewModels;
using Presentation_XinBeiYang.ViewModels.Settings;
using Presentation_XinBeiYang.Views;
using Presentation_XinBeiYang.Views.Settings;
using Prism.Ioc;
using Serilog;

namespace Presentation_XinBeiYang;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddPresentationCommonServices();
        containerRegistry.AddPhotoCamera();
        
        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<HttpClient>();
        
        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();
        
        // 注册设置窗口
        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
    }
    
    /// <summary>
    /// 启动
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
    }
    
    /// <summary>
    /// 退出
    /// </summary>
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            await Log.CloseAndFlushAsync();

            // 确保所有后台线程都已完成
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            await Log.CloseAndFlushAsync();
            await Task.Delay(500);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}